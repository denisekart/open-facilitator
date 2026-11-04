// The multi-box driver: the rendered bundle is pushed to the box and driven
// over ssh (lan/tailscale topologies). Invariants this keeps honest: node
// boxes expose NO inbound API surface (sshd only); the bundle is pushed
// rendered, never handwritten. A `local` image entry on a non-sim topology
// needs the repo on the box, so the ssh driver pushes a git archive of HEAD
// alongside the bundle (documented in README; a digest is the zero-dep path).
import path from "node:path";
import { must, run, fail } from "./util.js";
import type { RenderedBox } from "./render.js";
import { EXAMPLE_DIR } from "./render.js";
import type { RolesFile } from "./env.js";
import { type BoxDriver, sweepManaged } from "./compose.js";

export class SshDriver implements BoxDriver {
  constructor(
    readonly roles: RolesFile,
    readonly box: RenderedBox,
  ) {}

  /** box "kernel" -> kernel host; node slug -> that node's host. */
  private host(): string {
    if (this.box.kernelBox) return this.roles.kernel.host;
    const node = this.roles.nodes.find((n) => n.host.split(".")[0]?.toLowerCase() === this.box.name);
    if (node === undefined) fail(`roles: no node matches box "${this.box.name}"`);
    return node.host;
  }

  private target(): string {
    return `${this.roles.sshUser}@${this.host()}`;
  }

  /** Remote compose project directory: <remoteDir>/<env>/<box>. */
  private remoteDir(): string {
    return `${this.roles.remoteDir}/${this.roles.name}/${this.box.name}`;
  }

  private ssh(args: string[]): Promise<string> {
    return must(["ssh", "-o", "BatchMode=yes", this.target(), ...args]);
  }

  /** tar-pipe the rendered bundle over ssh (self-contained: compose.yaml,
   * .env, config/, units/, supervisor/, keycloak/, postgres-init/). */
  async push(): Promise<void> {
    const bundleParent = path.dirname(this.box.dir);
    await this.ssh(["mkdir", "-p", this.remoteDir()]);
    const push = await run([
      "tar", "-C", bundleParent, "-cf", "-", this.box.name,
    ]);
    if (push.code !== 0) fail(`tar bundle: ${push.stderr}`);
    await must(
      ["ssh", "-o", "BatchMode=yes", this.target(), "tar", "-C", this.remoteDir(), "-xf", "-"],
      { input: push.stdout },
    );
  }

  /** A `local` image build on the box needs the repo checkout at
   * <remoteDir> (the bundle's build context is ../../../../.. from the box
   * dir => <remoteDir>/.. x5 lands on the pushed repo root). */
  async pushRepo(): Promise<void> {
    await this.ssh(["mkdir", "-p", path.posix.dirname(path.posix.dirname(this.remoteDir()))]);
    const r = await run(["git", "-C", path.resolve(EXAMPLE_DIR, "..", ".."), "archive", "--format=tar", "HEAD"]);
    if (r.code !== 0) fail(`git archive HEAD: ${r.stderr.trim()} — a multi-box "local" image needs a git checkout`);
    await must(
      ["ssh", "-o", "BatchMode=yes", this.target(), "tar", "-C", path.posix.dirname(path.posix.dirname(this.remoteDir())), "-xf", "-"],
      { input: r.stdout },
    );
  }

  private compose(args: string[]): Promise<string> {
    // cd into the bundle dir so relative mounts + build contexts resolve;
    // compose reads the bundle's own compose.yaml + .env from there.
    return this.ssh(
      ["sh", "-c", `cd ${this.remoteDir()} && docker compose ${args.join(" ")}`],
    );
  }

  async up(_box: RenderedBox, opts: { build: boolean }): Promise<void> {
    await this.push();
    if (opts.build) {
      const needsRepo =
        this.roles.images.kernel === "local" || this.roles.images.node === "local";
      if (needsRepo) {
        console.warn(`warn: topology "${this.roles.topology}" with a "local" image pushes a repo archive to ${this.host()} (a digest avoids it)`);
        await this.pushRepo();
      }
    }
    await this.compose(["up", "-d"]);
  }

  async down(_box: RenderedBox, opts: { volumes: boolean }): Promise<void> {
    await this.compose(["down", ...(opts.volumes ? ["-v"] : [])]);
    await sweepManaged();
  }

  async logs(_box: RenderedBox): Promise<void> {
    process.stdout.write(await this.compose(["logs", "--tail", "100"]));
  }

  async ps(_box: RenderedBox): Promise<string> {
    return await this.compose(["ps", "--format", "json"]);
  }
}
