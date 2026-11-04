// The sim driver: every box is a local compose project on this host, all
// sharing the external `ofac-local` network. The multi-box driver (ssh.ts)
// implements the same surface over ssh. `localctl up` picks one by topology.
import { existsSync } from "node:fs";
import path from "node:path";
import { must, run, fail } from "./util.js";
import {
  EXAMPLE_DIR,
  LOCAL_KERNEL_IMAGE,
  LOCAL_NODE_IMAGE,
  LOCAL_WORKER_IMAGE,
  type RenderedBox,
} from "./render.js";
import { SHARED_NETWORK } from "./env.js";

export interface BoxDriver {
  up(box: RenderedBox, opts: { build: boolean }): Promise<void>;
  down(box: RenderedBox, opts: { volumes: boolean }): Promise<void>;
  logs(box: RenderedBox): Promise<void>;
  ps(box: RenderedBox): Promise<string>;
}

const composeProject = (box: RenderedBox, args: string[]): string[] => [
  "docker",
  "compose",
  "-p",
  box.composeProject,
  "-f",
  path.join(box.dir, "compose.yaml"),
  "--env-file",
  box.env.file,
  ...args,
];

/** Repo root: every local image builds from here (the bundle's build
 * context is the same path, five levels up from `.out/<env>/<box>`). */
export const REPO_ROOT = path.resolve(EXAMPLE_DIR, "..", "..");

/** Kernel/node images ride the repo Dockerfiles; the elastic worker layers
 * this example's config on the node image (repo-root context). */
export async function buildLocalImages(): Promise<void> {
  const build = async (tag: string, extra: string[]): Promise<void> => {
    console.log(`build ${tag}`);
    await must(["docker", "build", "-q", "-t", tag, ...extra, "."], { cwd: REPO_ROOT });
  };
  await build(LOCAL_KERNEL_IMAGE, ["-f", "src/facilitator/Dockerfile"]);
  await build(LOCAL_NODE_IMAGE, ["-f", "Dockerfile.node"]);
  await build(LOCAL_WORKER_IMAGE, [
    "--build-arg",
    `OFAC_BASE=${LOCAL_NODE_IMAGE}`,
    "-f",
    "examples/local/templates/kernel/Dockerfile.worker",
  ]);
}

async function ensureNetwork(): Promise<void> {
  const r = await run(["docker", "network", "inspect", SHARED_NETWORK]);
  if (r.code !== 0) await must(["docker", "network", "create", SHARED_NETWORK]);
}

/** Label-sweep kernel-provisioned workers (referencectl's sweep_managed). */
export async function sweepManaged(): Promise<void> {
  const r = await run([
    "docker",
    "ps",
    "-aq",
    "--filter",
    "label=io.facilitator.managed=kernel-provisioned",
  ]);
  const ids = r.stdout
    .trim()
    .split("\n")
    .filter((s) => s !== "");
  if (ids.length > 0) await run(["docker", "rm", "-f", ...ids]);
}

export const simDriver: BoxDriver = {
  async up(box, opts) {
    await ensureNetwork();
    if (!existsSync(path.join(box.dir, "compose.yaml")))
      fail(`${box.dir}:? no rendered bundle — run localctl render first`);
    if (opts.build && box.kernelBox) {
      await buildLocalImages();
    }
    await must(composeProject(box, ["up", "-d"]), { inherit: true });
  },
  async down(box, opts) {
    await must(composeProject(box, ["down", ...(opts.volumes ? ["-v"] : [])]), { inherit: true });
  },
  async logs(box) {
    await must(composeProject(box, ["logs", "--tail", "100"]), { inherit: true });
  },
  async ps(box) {
    return await must(composeProject(box, ["ps", "--format", "json"]));
  },
};
