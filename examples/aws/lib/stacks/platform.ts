import { CfnOutput, Stack, type StackProps } from "aws-cdk-lib";
import { CfnSecurityGroupIngress, Port } from "aws-cdk-lib/aws-ec2";
import type { Construct } from "constructs";
import { Vpc } from "../vpc.js";
import { resolveHostedZone } from "../hosted-zone.js";
import { Database } from "../database.js";
import { ArtifactBucket } from "../artifact-bucket.js";
import { SecretRef } from "../secret-ref.js";
import { Identity } from "../identity.js";
import { Cluster } from "../cluster.js";
import { WebFace } from "../web-face.js";
import { Waf } from "../waf.js";
import { KernelService } from "../kernel-service.js";
import { Observability } from "../observability.js";
import type { PlatformEnv } from "../env.js";

/**
 * The five platform stacks. Cross-stack flow is strictly one-way
 * (network → {data, identity} → kernel → observability): a stack only
 * consumes exports from stacks earlier in that chain, so any subset can
 * be deployed/destroyed in order. Destructive law: DataStack RETAINs the
 * cluster + bucket in prod/staging; tearing the rest down never deletes
 * the ledger.
 */

export interface PlatformStackProps extends StackProps {
  readonly envName: string;
  readonly platform: PlatformEnv;
  readonly production: boolean;
}

/** ofac-{env}-network: VPC (create-or-import), endpoints, baseline SG. */
export class NetworkStack extends Stack {
  readonly network: Vpc;

  constructor(scope: Construct, id: string, props: PlatformStackProps) {
    super(scope, id, props);
    this.network = new Vpc(this, "Vpc", {
      ...(props.platform.vpc ? { existing: props.platform.vpc } : {}),
      ...(props.platform.natMode ? { natMode: props.platform.natMode } : {}),
    });
  }
}

/** ofac-{env}-data: RDS + artifacts + the out-of-band secrets seam. */
export class DataStack extends Stack {
  readonly database: Database;
  readonly artifacts: ArtifactBucket;
  /**
   * Operator-managed seam: one placeholder secret (`facilitator/app`)
   * for values the kernel reads from Secrets Manager by ARN (webhook
   * signing seeds etc.). Created with a REPLACE_ME placeholder; the
   * hand-off is documented in the README.
   */
  readonly appSecret: SecretRef;

  constructor(
    scope: Construct,
    id: string,
    props: PlatformStackProps & { readonly network: NetworkStack },
  ) {
    super(scope, id, props);
    this.database = new Database(this, "Database", {
      vpc: props.network.network.vpc,
      vpcSubnets: props.network.network.privateSubnets,
      production: props.production,
      multiAz: props.platform.multiAz ?? true,
      // Env-distinct so one account can host several envs; the name is
      // the literal seam KernelStack uses for its dynamic reference.
      secretName: `facilitator/${props.envName}-db`,
    });
    this.artifacts = new ArtifactBucket(this, "Artifacts", {
      production: props.production,
    });
    this.appSecret = new SecretRef(this, "AppSecret", {
      secretName: "facilitator/app",
      initialPlaceholders: { value: "REPLACE_ME" },
      description: "ofac out-of-band app secrets (operator hand-off)",
    });
    // Pinned output names: `deployctl smoke` queries OutputKey~'ArtifactBucket'.
    new CfnOutput(this, "ArtifactBucketName", {
      value: this.artifacts.bucket.bucketName,
      description: "artifact-store bucket name",
    });
  }
}

/** ofac-{env}-identity: Cognito pool + clients + optional federation. */
export class IdentityStack extends Stack {
  readonly identity: Identity;

  constructor(scope: Construct, id: string, props: PlatformStackProps) {
    super(scope, id, props);
    this.identity = new Identity(this, "Identity", {
      domainPrefix: `${props.envName}-kernel`,
      kernelDomain: props.platform.kernelDomain,
      production: props.production,
      federation: props.platform.federation ?? null,
    });
    // Pinned output name: `deployctl smoke` queries OutputKey~'Issuer'.
    new CfnOutput(this, "OidcIssuer", {
      value: this.identity.issuer,
      description: "OIDC issuer (byte-match for identity:oidc:authority)",
    });
    new CfnOutput(this, "SpaClientId", {
      value: this.identity.spaClient.userPoolClientId,
      description: "SPA (PKCE) client id for the operator UI",
    });
  }
}

/**
 * ofac-{env}-kernel: cluster + web face + WAF + kernel service. The ALB
 * SG→kernel ingress rule and the DB ingress-SG join are wired here, after
 * both sides exist, keeping the construct graph cycle-free.
 */
export class KernelStack extends Stack {
  readonly kernel: KernelService;
  readonly face: WebFace;

  constructor(
    scope: Construct,
    id: string,
    props: PlatformStackProps & {
      readonly network: NetworkStack;
      readonly data: DataStack;
      readonly identity: IdentityStack;
      /** Resolved once in buildApp: sidecar config + alarms share it. */
      readonly metricNamespace?: string;
    },
  ) {
    super(scope, id, props);
    const cluster = new Cluster(this, "Cluster", { vpc: props.network.network.vpc });
    // enableWaf:false = no WebACL resource and no ALB association (dev cost).
    const waf = (props.platform.enableWaf ?? true) ? new Waf(this, "Waf") : undefined;
    this.kernel = new KernelService(this, "Kernel", {
      vpc: props.network.network.vpc,
      vpcSubnets: props.network.network.privateSubnets,
      cluster: cluster.cluster,
      imageUri: props.platform.images.kernel,
      dbSecretName: props.data.database.secretName,
      dbHost: props.data.database.dbHost,
      observability: props.platform.observability ?? true,
      ...(props.metricNamespace ? { metricNamespace: props.metricNamespace } : {}),
      artifactBucket: props.data.artifacts.bucket,
      oidcAuthority: props.identity.identity.issuer,
      oidcAudience: "facilitator-api",
      uiOidcAuthority: props.identity.identity.issuer,
      uiOidcClientId: props.identity.identity.spaClient.userPoolClientId,
    });
    this.face = new WebFace(this, "WebFace", {
      vpc: props.network.network.vpc,
      publicSubnets: props.network.network.publicSubnets ?? props.network.network.privateSubnets,
      zone: resolveHostedZone(this, props.platform.hostedZoneDomain, props.platform.hostedZoneId),
      kernelDomain: props.platform.kernelDomain,
      kernelTarget: this.kernel.targetGroup,
      userPool: props.identity.identity.userPool,
      spaClient: props.identity.identity.spaClient,
      cognitoDomainPrefix: `${props.envName}-kernel`,
      ...(waf ? { webAcl: waf.webAcl } : {}),
      infraAuth: props.platform.infraAuth ?? true,
    });
    // Sole ingress to the kernel ports: the face SG, wired post-construction.
    this.kernel.securityGroup.addIngressRule(
      this.face.securityGroup,
      Port.tcp(this.kernel.port),
      "web face",
    );
    // 5432 admission: the rule RESOURCE lives in this stack (kernel ->
    // data only). A rule added via `dbSg.connections` would render into
    // the DataStack template and point data -> kernel, closing a cycle.
    new CfnSecurityGroupIngress(this, "DbIngress", {
      groupId: props.data.database.securityGroup.securityGroupId,
      sourceSecurityGroupId: this.kernel.securityGroup.securityGroupId,
      ipProtocol: "tcp",
      fromPort: 5432,
      toPort: 5432,
      description: "kernel tasks -> RDS 5432",
    });
  }
}

/** ofac-{env}-observability: alarms, pager, ADOT publish grants. */
export class ObservabilityStack extends Stack {
  readonly observability: Observability;

  constructor(
    scope: Construct,
    id: string,
    props: PlatformStackProps & {
      readonly kernel: KernelStack;
      readonly alarmEmail?: string;
      readonly metricNamespace?: string;
    },
  ) {
    super(scope, id, props);
    this.observability = new Observability(this, "Observability", {
      ...(props.alarmEmail ? { alarmEmail: props.alarmEmail } : {}),
      ...(props.metricNamespace ? { metricNamespace: props.metricNamespace } : {}),
      kernelTaskRole: props.kernel.kernel.taskRole,
      loadBalancer: props.kernel.face.loadBalancer,
    });
  }
}
