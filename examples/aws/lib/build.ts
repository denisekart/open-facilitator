// The whole-stack wiring, as a factory so tests synth the exact tree the
// CLI builds. Selection law: names are `ofac-{env}-…`; every stack carries
// `ofac:role` (platform|team) + `ofac:env` so cost/audit filters work from
// day one. `smoke` (env name `smoke`) drops the stacks LocalStack cannot
// serve — a documented exclusion, not a growing branch.
import { DEFAULT_METRIC_NAMESPACE } from "./adot.js";
import { Tags } from "aws-cdk-lib";
import type { App } from "aws-cdk-lib";
import {
  DataStack,
  IdentityStack,
  KernelStack,
  NetworkStack,
  ObservabilityStack,
} from "./stacks/platform.js";
import { TeamFleetStack, TeamNetworkStack, TeamPlaneStack } from "./stacks/team.js";
import type { EnvFile } from "./env.js";

export interface BuildOptions {
  readonly app: App;
  readonly env: EnvFile;
  readonly envName: string;
  readonly alarmEmail?: string;
  readonly metricNamespace?: string;
}

export function buildApp(opts: BuildOptions): void {
  const { app, env, envName } = opts;
  const production = ["prod", "staging"].includes(envName);
  const platform = env.platform;
  // ONE namespace value for sidecar config AND alarms; precedence:
  // -c metricNamespace > env file platform.metricNamespace > default.
  const metricNamespace =
    opts.metricNamespace ?? platform.metricNamespace ?? DEFAULT_METRIC_NAMESPACE;
  const platformEnv = { account: platform.account, region: platform.region };
  const smoke = envName === "smoke";

  const network = new NetworkStack(app, `ofac-${envName}-network`, {
    env: platformEnv,
    envName,
    platform,
    production,
  });
  Tags.of(network).add("ofac:role", "platform");

  const data = new DataStack(app, `ofac-${envName}-data`, {
    env: platformEnv,
    envName,
    platform,
    production,
    network,
  });
  Tags.of(data).add("ofac:role", "platform");

  const identity = new IdentityStack(app, `ofac-${envName}-identity`, {
    env: platformEnv,
    envName,
    platform,
    production,
  });
  Tags.of(identity).add("ofac:role", "platform");

  if (!smoke) {
    const kernel = new KernelStack(app, `ofac-${envName}-kernel`, {
      env: platformEnv,
      envName,
      platform,
      production,
      network,
      data,
      identity,
      metricNamespace,
    });
    Tags.of(kernel).add("ofac:role", "platform");
    const observability = new ObservabilityStack(app, `ofac-${envName}-observability`, {
      env: platformEnv,
      envName,
      platform,
      production,
      kernel,
      metricNamespace,
      ...(typeof opts.alarmEmail === "string" ? { alarmEmail: opts.alarmEmail } : {}),
    });
    Tags.of(observability).add("ofac:role", "platform");
  }

  for (const team of env.teams) {
    const prefix = `ofac-${envName}-team-${team.name}`;
    const teamEnv = { account: team.account, region: team.region };
    const kernelUrl = `https://${platform.kernelDomain}`;
    const teamNetwork = new TeamNetworkStack(app, `${prefix}-network`, {
      env: teamEnv,
      envName,
      team,
      kernelUrl,
    });
    Tags.of(teamNetwork).add("ofac:role", "team");
    const plane = new TeamPlaneStack(app, `${prefix}-plane`, {
      env: teamEnv,
      envName,
      team,
      kernelUrl,
      network: teamNetwork,
      metricNamespace,
    });
    Tags.of(plane).add("ofac:role", "team");
    if (team.fleet) {
      const fleet = new TeamFleetStack(app, `${prefix}-fleet`, {
        env: teamEnv,
        envName,
        team,
        kernelUrl,
        network: teamNetwork,
        plane,
      });
      Tags.of(fleet).add("ofac:role", "team");
    }
  }

  Tags.of(app).add("ofac:env", envName);
}
