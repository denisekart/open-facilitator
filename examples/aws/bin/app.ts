// CDK CLI entry: read `-c envFile=…` ONCE, parse, hand the whole tree to
// buildApp (the same factory the tests synth). Stacks take their account +
// region from the env file — never from the ambient CLI profile.
import { App } from "aws-cdk-lib";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { buildApp } from "../lib/build.js";
import { loadEnvFile } from "../lib/env.js";

const app = new App();
const envFileArg = app.node.tryGetContext("envFile");
if (typeof envFileArg !== "string" || envFileArg === "") {
  throw new Error("missing -c envFile=envs/<name>.json (see envs/example.json)");
}
const envPath = resolve(process.cwd(), envFileArg);
const env = loadEnvFile(JSON.parse(readFileSync(envPath, "utf8")));
const envName = envFileArg.replace(/^.*\//, "").replace(/\.json$/, "");
const alarmEmail = app.node.tryGetContext("alarmEmail");
const metricNamespace = app.node.tryGetContext("metricNamespace");

buildApp({
  app,
  env,
  envName,
  ...(typeof alarmEmail === "string" ? { alarmEmail } : {}),
  ...(typeof metricNamespace === "string" ? { metricNamespace } : {}),
});
