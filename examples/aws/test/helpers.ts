// Offline synth harness: build the SAME tree the CLI builds (buildApp),
// but pre-seed the AZ context so nothing needs AWS credentials — the
// committed cdk.context.json carries the identical entries for `cdk synth`.
import { App, Stack } from "aws-cdk-lib";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import type { IConstruct } from "constructs";
import { buildApp } from "../lib/build.js";
import { loadEnvFile } from "../lib/env.js";
import type { EnvFile } from "../lib/env.js";

const here = fileURLToPath(new URL(".", import.meta.url));

export function exampleEnv(): EnvFile {
  return loadEnvFile(JSON.parse(readFileSync(resolve(here, "../envs/example.json"), "utf8")));
}

export interface SynthOptions {
  readonly env?: EnvFile;
}

export function synthApp(opts: SynthOptions = {}): {
  app: App;
  stacks: Map<string, Stack>;
} {
  const env = opts.env ?? exampleEnv();
  const accounts = [env.platform.account, ...env.teams.map((t) => t.account)];
  const regions = [env.platform.region, ...env.teams.map((t) => t.region)];
  const context: Record<string, unknown> = {
    envFile: "envs/example.json",
    "aws:cdk:enable-path-metadata": false,
  };
  for (const account of accounts) {
    for (const region of new Set(regions)) {
      context[`availability-zones:account=${account}:region=${region}`] = [
        `${region}a`,
        `${region}b`,
      ];
    }
  }
  const app = new App({ context });
  buildApp({ app, env, envName: "example" });
  const stacks = new Map<string, Stack>();
  const walk = (node: IConstruct) => {
    if (node instanceof Stack) stacks.set(node.stackName, node);
    for (const child of node.node.children) walk(child);
  };
  for (const child of app.node.children) walk(child);
  return { app, stacks };
}

export function stackOf(stacks: Map<string, Stack>, suffix: string): Stack {
  const stack = stacks.get(`ofac-example-${suffix}`);
  if (!stack) throw new Error(`stack ofac-example-${suffix} missing`);
  return stack;
}
