import { RemovalPolicy } from "aws-cdk-lib";
import {
  AuroraPostgresEngineVersion,
  ClusterInstance,
  DatabaseCluster,
  DatabaseClusterEngine,
  ParameterGroup,
} from "aws-cdk-lib/aws-rds";
import type { ISecret } from "aws-cdk-lib/aws-secretsmanager";
import { SecurityGroup } from "aws-cdk-lib/aws-ec2";
import type { IVpc, SubnetSelection } from "aws-cdk-lib/aws-ec2";
import type { IKey } from "aws-cdk-lib/aws-kms";
import { Key } from "aws-cdk-lib/aws-kms";
import { Construct } from "constructs";

/**
 * Postgres 17 Aurora-compatible, **Serverless v2 only**. Serverless v2 is
 * a cluster capability, so the data plane is a single-writer Aurora-
 * compatible cluster: ACU autoscaling from 0.5 is the only shape cheap in
 * dev and hands-off in prod without a second deployment tier, and the
 * single writer fits `facilitator.outbox` + advisory locks exactly. Why
 * not provisioned instances: idle-time billing. `DatabaseProxy` omitted:
 * connection storms are a scale-out concern the two-replica kernel shape
 * doesn't reach.
 *
 * Security posture: private subnets only, SG ingress 5432 from the kernel
 * SG and nothing else, CMK storage encryption, TLS-enforced parameter
 * group, deletion protection on prod. The auto-created master secret
 * arrives under the CMK; consumers bind to `secret.secretArn` (the
 * CloudFormation-generated name, pinned via `secretName` here).
 */
export interface DatabaseProps {
  readonly vpc: IVpc;
  readonly vpcSubnets: SubnetSelection;
  /** Prod posture: DeletionProtection + Retain (+ reader unless
   *  multiAz:false). */
  readonly production?: boolean;
  /** AZ-spanning posture (default true): an Aurora reader beside the
   *  writer. Aurora has no per-instance MultiAZ — a second AZ *is* a
   *  reader instance. Distinct from `production`. */
  readonly multiAz?: boolean;
  readonly minAcu?: number;
  readonly maxAcu?: number;
  /** CMK for storage/secret; defaults to a stack-local CMK. */
  readonly encryptionKey?: IKey;
  /** Master-secret NAME (default `facilitator/db`). Deterministic and
   *  literal so consumers can build dynamic references to it without a
   *  cross-stack Ref; env-distinct when one account hosts several envs. */
  readonly secretName?: string;
  // (The 5432 rule is NOT declared here: a rule whose source is the
  // kernel SG would strand its rule resource in the DataStack template
  // and point data -> kernel. KernelStack owns the admission as a
  // standalone CfnSecurityGroupIngress, so the stack graph stays one-way.)
}

export class Database extends Construct {
  readonly cluster: DatabaseCluster;
  readonly encryptionKey: IKey;
  readonly secret: ISecret;
  /** RDS SG; admission (5432 from the kernel SG) is owned by KernelStack. */
  readonly securityGroup: SecurityGroup;
  /** Literal master-secret name (see props.secretName). */
  readonly secretName: string;
  /** Writer endpoint address (token): the kernel builds its Npgsql
   *  connection string from this + the secret's password field. */
  get dbHost(): string {
    return this.cluster.clusterEndpoint.hostname;
  }

  constructor(scope: Construct, id: string, props: DatabaseProps) {
    super(scope, id);
    const prod = props.production ?? false;
    this.secretName = props.secretName ?? "facilitator/db";
    this.encryptionKey =
      props.encryptionKey ??
      new Key(this, "KmsKey", {
        enableKeyRotation: true,
        description: "ofac RDS storage + master-secret CMK",
      });
    this.securityGroup = new SecurityGroup(this, "DbSg", {
      vpc: props.vpc,
      allowAllOutbound: false,
      description: "ofac RDS: 5432 admitted by a kernel-stack-owned rule",
    });
    const engine = DatabaseClusterEngine.auroraPostgres({
      version: AuroraPostgresEngineVersion.VER_17_5,
    });
    this.cluster = new DatabaseCluster(this, "Cluster", {
      engine,
      // Serverless v2, always: 0.5→2 ACU covers a demo through a busy
      // team. The writer alone runs in one AZ; `multiAz` (default true)
      // places a reader beside it — Aurora's shape of MultiAZ.
      serverlessV2MinCapacity: props.minAcu ?? 0.5,
      serverlessV2MaxCapacity: props.maxAcu ?? 2,
      // Instances carry the public/private placement law.
      writer: ClusterInstance.serverlessV2("Writer", { publiclyAccessible: false }),
      ...((props.multiAz ?? true)
        ? {
            readers: [ClusterInstance.serverlessV2("Reader", { publiclyAccessible: false })],
          }
        : {}),
      vpc: props.vpc,
      vpcSubnets: props.vpcSubnets,
      securityGroups: [this.securityGroup],
      storageEncrypted: true,
      storageEncryptionKey: this.encryptionKey,
      deletionProtection: prod,
      removalPolicy: prod ? RemovalPolicy.RETAIN : RemovalPolicy.SNAPSHOT,
      defaultDatabaseName: "facilitator",
      credentials: {
        username: "facilitator",
        secretName: this.secretName,
        encryptionKey: this.encryptionKey,
      },
      // TLS is non-negotiable on the wire, even inside the VPC.
      parameterGroup: new ParameterGroup(this, "Params", {
        engine,
        parameters: { "rds.force_ssl": "1" },
      }),
      cloudwatchLogsExports: ["postgresql"],
    });
    this.secret = this.cluster.secret!;
  }
}
