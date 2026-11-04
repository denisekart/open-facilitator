// The env-file contract (`envs/<name>.json`), read once by bin/app.ts.
// Create-or-reference law: company-owned things (VPC, DNS zone) carry an
// imported id when pre-owned, null when the example creates them. Local
// infra (RDS, S3, secrets, EFS) is always created. Image URIs are
// digest-pinned strings — the registry is opaque (ghcr today, GitLab/ECR
// later), so there is no registry enum anywhere in the code.

/** Pre-owned VPC to import instead of creating. */
export interface ExistingVpc {
  readonly vpcId: string;
  readonly privateSubnetIds: string[];
  /** Public subnets are only needed where a WebFace lands (platform). */
  readonly publicSubnetIds?: string[];
}

/** Digest-pinned container images this environment deploys. */
export interface Images {
  readonly kernel: string;
  readonly node: string;
}

/** Corporate-IdP federation for the Cognito pool (optional). */
export interface Federation {
  readonly type: "oidc" | "saml";
  readonly name: string;
  readonly metadataUrl?: string;
  readonly clientId?: string;
  /** OIDC client secret; supplied via cdk context, never committed. */
  readonly clientSecret?: string;
  readonly metadataXml?: string;
}

export interface PlatformEnv {
  readonly account: string;
  readonly region: string;
  /** Route53 zone looked up by domain — must pre-exist (create-or-reference). */
  readonly hostedZoneDomain: string;
  readonly kernelDomain: string;
  /** null ⇒ NetworkStack creates a VPC; set ⇒ import by id. */
  readonly vpc: ExistingVpc | null;
  readonly images: Images;
  /** OPTIONAL: explicit Route53 zone id (offline import path; see
   * hosted-zone.ts). Absent ⇒ lookup with OFAC_AWS_LOOKUP, placeholder
   * + warning otherwise. */
  readonly hostedZoneId?: string;
  /** OPTIONAL: infra-level ALB forward-auth on the admin/SPA paths
   * (default true). false = kernel app-level OIDC authz only. */
  readonly infraAuth?: boolean;
  /** OPTIONAL: ADOT sidecar + observability plane (default true). */
  readonly observability?: boolean;
  /** OPTIONAL: dev/prod posture flag (default false). */
  readonly production?: boolean;
  /** OPTIONAL: WAFv2 web ACL on the ALB (default true). false = no
   *  WebACL resource and no association — a dev-cost knob. */
  readonly enableWaf?: boolean;
  /** OPTIONAL: VPC NAT posture (default 'gateway'). 'none' is the
   *  LocalStack-smoke shape: LocalStack 4's CFN cannot provision a
   *  NatGateway, so the smoke env asks for the topology minus the gateway. */
  readonly natMode?: "gateway" | "public" | "none";
  /** OPTIONAL: RDS spanning AZs — an Aurora reader instance beside the
   *  writer (default true, distinct from `production`, which additionally
   *  arms deletion protection + RETAIN). */
  readonly multiAz?: boolean;
  /** OPTIONAL: OTLP→CloudWatch custom-metric namespace (default
   *  `facilitator`). ONE value feeds the ADOT sidecar config and the
   *  alarms; confirm at first real deploy, never in code. */
  readonly metricNamespace?: string;
  /** OPTIONAL: alarm pager email for the observability plane. */
  readonly alarmEmail?: string;
  readonly federation?: Federation | null;
}

export interface TeamEnv {
  readonly name: string;
  readonly account: string;
  readonly region: string;
  /** null ⇒ TeamNetworkStack creates a VPC; set ⇒ import by id. */
  readonly vpc: ExistingVpc | null;
  /** Opt-in fleet capability (TeamFleetStack); default off. */
  readonly fleet?: boolean;
  /** OPTIONAL: ADOT sidecar on the plane's tasks (default off — the
   *  team account carries its own self-contained OTLP publish policy;
   *  no ref into the platform Observability stack). */
  readonly observability?: boolean;
  readonly images: {
    readonly gateway: string;
    readonly workspace: string;
    readonly fleetLauncher?: string;
  };
  /** OPTIONAL: how many workspace services the plane runs (default 1). */
  readonly workspaces?: number;
}

export interface EnvFile {
  readonly platform: PlatformEnv;
  readonly teams: TeamEnv[];
}

/** Fail with an operator-readable message (create-or-reference law). */
export function requireEnv(value: string | null | undefined, what: string): string {
  if (!value || value.trim() === "") {
    throw new Error(
      `env file: ${what} is missing — set it in envs/*.json (create-or-reference law).`,
    );
  }
  return value;
}

export function loadEnvFile(raw: unknown): EnvFile {
  const env = raw as EnvFile;
  if (!env || typeof env !== "object" || !env.platform || !Array.isArray(env.teams)) {
    throw new Error("env file: expected { platform: {...}, teams: [...] }");
  }
  requireEnv(env.platform.account, "platform.account");
  requireEnv(env.platform.region, "platform.region");
  requireEnv(env.platform.hostedZoneDomain, "platform.hostedZoneDomain");
  requireEnv(env.platform.kernelDomain, "platform.kernelDomain");
  requireEnv(env.platform.images?.kernel, "platform.images.kernel");
  requireEnv(env.platform.images?.node, "platform.images.node");
  for (const team of env.teams) {
    requireEnv(team.name, "teams[].name");
    requireEnv(team.account, `team ${team.name}: account`);
    requireEnv(team.region, `team ${team.name}: region`);
    requireEnv(team.images?.gateway, `team ${team.name}: images.gateway`);
    requireEnv(team.images?.workspace, `team ${team.name}: images.workspace`);
    if (team.fleet) {
      requireEnv(
        team.images?.fleetLauncher,
        `team ${team.name}: images.fleetLauncher (fleet: true)`,
      );
    }
  }
  return env;
}
