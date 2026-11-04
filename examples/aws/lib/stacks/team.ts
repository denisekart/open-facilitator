import { Stack, Tags, type StackProps } from "aws-cdk-lib";
import type { Construct } from "constructs";
import { Vpc } from "../vpc.js";
import { Cluster } from "../cluster.js";
import { TeamPlane } from "../team-plane.js";
import { Fleet } from "../fleet.js";
import { SecretRef } from "../secret-ref.js";
import type { TeamEnv } from "../env.js";

/**
 * Team stacks: network → plane → (opt-in) fleet, one-way like the
 * platform chain. The plane is outbound-only; the fleet stack is the
 * optional capability (env `"fleet": true`) — absent by default, exactly
 * like the compose provisioning overlay.
 */

export interface TeamStackProps extends StackProps {
  readonly envName: string;
  readonly team: TeamEnv;
  /** Public kernel origin the plane dials out to. */
  readonly kernelUrl: string;
}

/** ofac-{env}-team-{name}-network */
export class TeamNetworkStack extends Stack {
  readonly network: Vpc;

  constructor(scope: Construct, id: string, props: TeamStackProps) {
    super(scope, id, props);
    this.network = new Vpc(this, "Vpc", {
      ...(props.team.vpc ? { existing: props.team.vpc } : {}),
    });
  }
}

/** ofac-{env}-team-{name}-plane: cluster + gateway/workspace + EFS + secrets. */
export class TeamPlaneStack extends Stack {
  readonly plane: TeamPlane;
  /** One cluster per team; the fleet stack rides it. */
  readonly cluster: Cluster;
  /** Per-team link enrollment secret (placeholder; operator hand-off). */
  readonly linkSecret: SecretRef;

  constructor(
    scope: Construct,
    id: string,
    props: TeamStackProps & {
      readonly network: TeamNetworkStack;
      /** Resolved once in buildApp (sidecar config; alarms if added). */
      readonly metricNamespace?: string;
    },
  ) {
    super(scope, id, props);
    this.cluster = new Cluster(this, "Cluster", { vpc: props.network.network.vpc });
    this.linkSecret = new SecretRef(this, "LinkSecret", {
      secretName: `ofac/${props.team.name}/link`,
      initialPlaceholders: { linkToken: "REPLACE_ME" },
      description: `ofac ${props.team.name}: link enrollment secret (operator hand-off)`,
    });
    this.plane = new TeamPlane(this, "Plane", {
      vpc: props.network.network.vpc,
      vpcSubnets: props.network.network.privateSubnets,
      cluster: this.cluster.cluster,
      kernelUrl: props.kernelUrl,
      gateway: {
        imageUri: props.team.images.gateway,
        statePath: "/var/lib/ofac/gateway",
        uid: "1001",
      },
      workspace: {
        imageUri: props.team.images.workspace,
        statePath: "/var/lib/ofac/workspace",
        uid: "1002",
      },
      linkSecretArns: { OFAC_LINK_TOKEN: this.linkSecret.secret.secretArn },
      fleetEnabled: props.team.fleet === true,
      observability: props.team.observability === true,
      ...(props.metricNamespace ? { metricNamespace: props.metricNamespace } : {}),
    });
    Tags.of(this).add("ofac:team", props.team.name);
  }
}

/**
 * ofac-{env}-team-{name}-fleet: only when the env file opts the team in.
 * The launcher task definition family (the RunTask target), its baseline
 * service, and the EFS workspace mount. Non-production reference.
 */
export class TeamFleetStack extends Stack {
  readonly fleet: Fleet;

  constructor(
    scope: Construct,
    id: string,
    props: TeamStackProps & {
      readonly network: TeamNetworkStack;
      readonly plane: TeamPlaneStack;
    },
  ) {
    super(scope, id, props);
    // The plane owns the volume AND the launcher's SG (mount-target
    // admission lives with the volume); this stack only imports them.
    const fleetSg = props.plane.plane.fleetSg;
    if (!fleetSg) {
      throw new Error("TeamFleetStack: the team plane must run fleetEnabled (team `fleet: true`)");
    }
    this.fleet = new Fleet(this, "Fleet", {
      vpc: props.network.network.vpc,
      vpcSubnets: props.network.network.privateSubnets,
      // Launcher tasks ride the team cluster — one cluster per team.
      cluster: props.plane.cluster.cluster,
      launcherImage: props.team.images.fleetLauncher ?? props.team.images.workspace,
      secrets: {
        OFAC_LINK_TOKEN: props.plane.linkSecret.secret.secretArn,
      },
      kernelUrl: props.kernelUrl,
      securityGroup: fleetSg,
      efs: {
        fileSystem: props.plane.plane.fileSystem,
        accessPointId: props.plane.plane.fleetAccessPointId,
      },
    });
    Tags.of(this).add("ofac:team", props.team.name);
  }
}
