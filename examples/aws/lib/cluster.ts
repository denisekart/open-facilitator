import { Cluster as EcsCluster } from "aws-cdk-lib/aws-ecs";
import type { ICluster } from "aws-cdk-lib/aws-ecs";
import type { IVpc } from "aws-cdk-lib/aws-ec2";
import { Construct } from "constructs";

/**
 * One ECS cluster per account; Fargate capacity providers only — no EC2
 * capacity anywhere, so the fleet needs no host patching story. The
 * cluster must ride the plane's VPC: its own default-VPC fallback would
 * give services a different subnet law than every other construct (task
 * placement itself is per-service). Container insights costs money per
 * metric stream, so it stays off (the default).
 */
export interface ClusterProps {
  readonly vpc: IVpc;
}

export class Cluster extends Construct {
  readonly cluster: ICluster;

  constructor(scope: Construct, id: string, props: ClusterProps) {
    super(scope, id);
    this.cluster = new EcsCluster(this, "Cluster", {
      vpc: props.vpc,
    });
  }
}
