import { Duration, Stack } from "aws-cdk-lib";
import { Peer, Port, SecurityGroup } from "aws-cdk-lib/aws-ec2";
import type { ISecurityGroup, IVpc, SubnetSelection } from "aws-cdk-lib/aws-ec2";
import { CfnWebACL, CfnWebACLAssociation } from "aws-cdk-lib/aws-wafv2";
import {
  ApplicationLoadBalancer,
  ApplicationProtocol,
  ListenerAction,
  ListenerCondition,
  SslPolicy,
  UnauthenticatedAction,
} from "aws-cdk-lib/aws-elasticloadbalancingv2";
import type { IApplicationTargetGroup } from "aws-cdk-lib/aws-elasticloadbalancingv2";
import { AuthenticateCognitoAction } from "aws-cdk-lib/aws-elasticloadbalancingv2-actions";
import { CfnRecordSet, RecordType } from "aws-cdk-lib/aws-route53";
import type { IHostedZone } from "aws-cdk-lib/aws-route53";
import { Certificate, CertificateValidation } from "aws-cdk-lib/aws-certificatemanager";
import { Bucket } from "aws-cdk-lib/aws-s3";
import type { IBucket } from "aws-cdk-lib/aws-s3";
import { BlockPublicAccess, BucketEncryption } from "aws-cdk-lib/aws-s3";
import { StringParameter } from "aws-cdk-lib/aws-ssm";
import { UserPoolDomain } from "aws-cdk-lib/aws-cognito";
import type { IUserPool, IUserPoolClient } from "aws-cdk-lib/aws-cognito";
import { Construct } from "constructs";

/**
 * The single web face: one internet-facing ALB, one cert, one DNS name.
 * Listener rules are exact-literal path matches, ordered by priority:
 *  1. (10) `/api/ingress/*` — provider webhooks: public, WAF rate-limited.
 *  2. (20) the link-plane door — `/api/link/v1/*`, `/hubs/link`,
 *     `/events`, `/deliveries`, `/health`: the endpoints workspaces,
 *     gateways and the fleet launcher dial OUT to; public by law (link
 *     tokens authenticate, not browser sessions).
 *  3. (100) `/api/admin/*`, `/api/works*`, `/ui-config` and the SPA
 *     default — behind ALB forward-auth to Cognito when `infraAuth` is
 *     on; the kernel's app-level OIDC authz enforces underneath either
 *     way. Webhook/link rules are never infra-authed.
 * The ALB is the only component that touches public subnets —
 * internet-facing is its entire job.
 */
export interface WebFaceProps {
  readonly vpc: IVpc;
  /** MUST resolve to public subnets; the face is internet-facing. */
  readonly publicSubnets: SubnetSelection;
  /** Resolved by app.ts (create-or-reference): explicit id, lookup when
   * OFAC_AWS_LOOKUP is set, placeholder offline (see hosted-zone.ts). */
  readonly zone: IHostedZone;
  readonly kernelDomain: string;
  readonly kernelTarget: IApplicationTargetGroup;
  /** Infra hop on the admin/SPA paths; kernel app-level authz always applies. */
  readonly infraAuth?: boolean;
  readonly userPool?: IUserPool;
  readonly spaClient?: IUserPoolClient;
  /** Managed-login prefix for the forward-auth redirect. */
  readonly cognitoDomainPrefix?: string;
  readonly webAcl?: CfnWebACL;
}

export class WebFace extends Construct {
  readonly loadBalancer: ApplicationLoadBalancer;
  readonly certificate: Certificate;
  readonly zone: IHostedZone;
  /** Face SG — KernelService takes it as the only ingress source. */
  readonly securityGroup: ISecurityGroup;

  constructor(scope: Construct, id: string, props: WebFaceProps) {
    super(scope, id);
    const infraAuth = props.infraAuth ?? true;
    if (infraAuth && (!props.userPool || !props.spaClient || !props.cognitoDomainPrefix)) {
      throw new Error(
        "WebFace: infraAuth requires userPool + spaClient + cognitoDomainPrefix (or pass infraAuth: false for app-level auth only).",
      );
    }
    this.zone = props.zone;
    this.certificate = new Certificate(this, "Cert", {
      domainName: props.kernelDomain,
      subjectAlternativeNames: [`*.${this.zone.zoneName}`],
      validation: CertificateValidation.fromDns(this.zone),
    });
    const sg = new SecurityGroup(this, "Sg", {
      vpc: props.vpc,
      allowAllOutbound: false,
      description: "ofac web face: 443/80 from anywhere",
    });
    for (const peer of [Peer.anyIpv4(), Peer.anyIpv6()]) {
      sg.addIngressRule(peer, Port.tcp(443), "https");
      sg.addIngressRule(peer, Port.tcp(80), "http redirect");
    }
    this.securityGroup = sg;
    this.loadBalancer = new ApplicationLoadBalancer(this, "Alb", {
      vpc: props.vpc,
      vpcSubnets: props.publicSubnets,
      internetFacing: true,
      securityGroup: sg,
      http2Enabled: true,
      dropInvalidHeaderFields: true,
      idleTimeout: Duration.seconds(60),
      deletionProtection: true,
    });
    const albLogs: IBucket = new Bucket(this, "AlbLogs", {
      encryption: BucketEncryption.S3_MANAGED,
      blockPublicAccess: BlockPublicAccess.BLOCK_ALL,
      enforceSSL: true,
      lifecycleRules: [{ expiration: Duration.days(90) }],
    });
    this.loadBalancer.logAccessLogs(albLogs, "alb/");
    if (props.webAcl) {
      new CfnWebACLAssociation(this, "Waf", {
        resourceArn: this.loadBalancer.loadBalancerArn,
        webAclArn: props.webAcl.attrArn,
      });
    }
    this.loadBalancer.addListener("Http", {
      port: 80,
      protocol: ApplicationProtocol.HTTP,
      open: true,
      defaultAction: ListenerAction.redirect({
        port: "443",
        protocol: "HTTPS",
        permanent: true,
      }),
    });
    const forward = ListenerAction.forward([props.kernelTarget]);
    let domainImports = 0;
    const auth = (next: ListenerAction): ListenerAction =>
      new AuthenticateCognitoAction({
        userPool: props.userPool!,
        userPoolClient: props.spaClient!,
        userPoolDomain: UserPoolDomain.fromDomainName(
          this,
          `AuthDomain${domainImports++}`,
          `${props.cognitoDomainPrefix}.auth.${Stack.of(this).region}.amazoncognito.com`,
        ),
        next,
        onUnauthenticatedRequest: UnauthenticatedAction.AUTHENTICATE,
        scope: "openid email profile",
        sessionTimeout: Duration.hours(8),
      });
    const https = this.loadBalancer.addListener("Https", {
      port: 443,
      protocol: ApplicationProtocol.HTTPS,
      certificates: [this.certificate],
      // TLS 1.2+1.3, the console-recommended policy.
      sslPolicy: SslPolicy.RECOMMENDED_TLS,
      open: true,
      // SPA + admin default: authed when infraAuth.
      defaultAction: infraAuth ? auth(forward) : forward,
    });
    // Rule 1: provider webhooks.
    https.addAction("WebhookRule", {
      priority: 10,
      conditions: [ListenerCondition.pathPatterns(["/api/ingress/*"])],
      action: forward,
    });
    // Rule 2: the link-plane door.
    https.addAction("LinkRule", {
      priority: 20,
      conditions: [
        ListenerCondition.pathPatterns([
          "/api/link/v1/*",
          "/hubs/link",
          "/events",
          "/deliveries",
          "/health",
        ]),
      ],
      action: forward,
    });
    // Rule 3: explicit admin paths — authed even if the default shape
    // ever changes.
    if (infraAuth) {
      https.addAction("AdminRule", {
        priority: 100,
        conditions: [ListenerCondition.pathPatterns(["/api/admin/*", "/api/works*", "/ui-config"])],
        action: auth(forward),
      });
    }
    // DNS: the kernel name points at the face.
    new CfnRecordSet(this, "Alias", {
      hostedZoneId: this.zone.hostedZoneId,
      name: props.kernelDomain,
      type: RecordType.A,
      aliasTarget: {
        dnsName: this.loadBalancer.loadBalancerDnsName,
        hostedZoneId: this.loadBalancer.loadBalancerCanonicalHostedZoneId,
        evaluateTargetHealth: false,
      },
    });
    new StringParameter(this, "AlbDns", {
      parameterName: `/ofac/${props.kernelDomain}/alb-dns`,
      stringValue: this.loadBalancer.loadBalancerDnsName,
    });
  }
}
