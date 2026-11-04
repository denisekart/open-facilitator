import {
  GatewayVpcEndpointAwsService,
  InterfaceVpcEndpointAwsService,
  IpAddresses,
  SecurityGroup,
  SubnetType,
  Vpc as VpcL2,
} from "aws-cdk-lib/aws-ec2";
import type { IVpc, SubnetSelection } from "aws-cdk-lib/aws-ec2";
import { Fn, Stack } from "aws-cdk-lib";
import { Construct } from "constructs";
import type { ExistingVpc } from "./env.js";

/**
 * Create-or-reference network block: two AZs, PRIVATE subnets only for
 * workloads (no public IPs anywhere), NAT egress, and private interface
 * endpoints so the account never hairpins through the internet for the AWS
 * services the kernel/team plane depends on. An imported VPC must already
 * satisfy that shape; `publicSubnetIds` are only ever consumed by WebFace.
 */
export interface VpcProps {
  /** Set ⇒ import by id; undefined ⇒ create. */
  readonly existing?: ExistingVpc;
  /** 'gateway' = VPC gateway-endpoint NAT (one AZ, cheap demo); 'public' = per-AZ NAT. */
  readonly natMode?: "gateway" | "public" | "none";
}

export class Vpc extends Construct {
  readonly vpc: IVpc;
  readonly privateSubnets: SubnetSelection;
  readonly publicSubnets?: SubnetSelection;
  /** Baseline SG (egress-only posture starts here; services add ingress). */
  readonly baselineSg: SecurityGroup;

  constructor(scope: Construct, id: string, props: VpcProps = {}) {
    super(scope, id);
    if (props.existing) {
      const { vpcId, privateSubnetIds, publicSubnetIds } = props.existing;
      this.vpc = VpcL2.fromVpcAttributes(this, "Imported", {
        vpcId,
        // AZ↔subnet pairing is irrelevant to consumers here (they place by
        // id); the region's AZ list keeps CFN's length law satisfied for
        // the two-subnet minimum the example imports.
        availabilityZones: Fn.getAzs(Stack.of(this).region),
        privateSubnetIds,
        ...(publicSubnetIds ? { publicSubnetIds } : {}),
      });
      this.privateSubnets = { subnets: this.vpc.privateSubnets };
      this.publicSubnets = publicSubnetIds ? { subnets: this.vpc.publicSubnets } : undefined;
    } else {
      const nat = props.natMode ?? "gateway";
      // 'gateway' (cheap demo shape): private subnets have NO direct egress;
      // one public subnet hosts the single NAT, and S3/endpoint traffic rides
      // the gateway/interface endpoints below. 'public': per-AZ NAT.
      // 'none' (LocalStack smoke posture): the same isolated shape minus the
      // gateway — LocalStack 4's CFN cannot provision a NatGateway (its EIP
      // wiring returns an unusable allocation id).
      const created = new VpcL2(this, "Created", {
        ipAddresses: IpAddresses.cidr("10.42.0.0/16"),
        maxAzs: 2,
        natGateways: nat === "gateway" ? 1 : nat === "public" ? 2 : 0,
        subnetConfiguration: [
          {
            // Vpc L2 capitalizes group names; consumers below must match.
            name: "Private",
            subnetType:
              nat === "public" ? SubnetType.PRIVATE_WITH_EGRESS : SubnetType.PRIVATE_ISOLATED,
            cidrMask: 24,
          },
          {
            name: nat === "public" ? "Public" : "Nat",
            subnetType: SubnetType.PUBLIC,
            cidrMask: 28,
          },
        ],
      });
      // S3 via gateway endpoint: free, and the artifact-store path never
      // leaves the AWS backbone.
      created.addGatewayEndpoint("S3Endpoint", {
        service: GatewayVpcEndpointAwsService.S3,
      });
      for (const [name, service] of [
        ["SecretsManager", InterfaceVpcEndpointAwsService.SECRETS_MANAGER],
        ["EcrApi", InterfaceVpcEndpointAwsService.ECR],
        ["EcrDocker", InterfaceVpcEndpointAwsService.ECR_DOCKER],
        ["CloudWatchLogs", InterfaceVpcEndpointAwsService.CLOUDWATCH_LOGS],
        ["Cognito", InterfaceVpcEndpointAwsService.COGNITO_IDP],
        ["Sts", InterfaceVpcEndpointAwsService.STS],
      ] as const) {
        created.addInterfaceEndpoint(name, {
          service,
          privateDnsEnabled: true,
        });
      }
      this.vpc = created;
      this.privateSubnets = { subnetGroupName: "Private" };
      this.publicSubnets = { subnetGroupName: nat === "gateway" ? "Nat" : "Public" };
    }
    this.baselineSg = new SecurityGroup(this, "BaselineSg", {
      vpc: this.vpc,
      allowAllOutbound: true,
      description: "ofac baseline: egress-only, services add their own ingress",
    });
  }
}
