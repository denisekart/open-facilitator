// Driver dispatch + the command verbs that ride it: up/down/logs. The
// topology decides: sim -> local compose projects; lan/tailscale -> the ssh
// driver, one fresh instance per box (it carries the box + host mapping).
// These verbs ride the RENDERED bundles (loadBundles) — never a re-render —
// so an `up` after `enroll-token` keeps the written OFAC_ENROLL_SECRET.
import { loadBundles, hostKernelUrl, render, EXAMPLE_DIR, type RenderedBox } from "./render.js";
import { loadRoles, type RolesFile } from "./env.js";
import { simDriver, sweepManaged, type BoxDriver } from "./compose.js";
import { SshDriver } from "./ssh.js";
import { fail } from "./util.js";

export function driverFor(roles: RolesFile, box: RenderedBox): BoxDriver {
  if (roles.topology === "sim") return simDriver;
  return new SshDriver(roles, box);
}

export function pickBox(boxes: RenderedBox[], name: string | undefined): RenderedBox[] {
  if (name === undefined) return boxes;
  const hit = boxes.filter((b) => b.name === name);
  if (hit.length === 0)
    fail(`no box "${name}" — rendered boxes: ${boxes.map((b) => b.name).join(", ")}`);
  return hit;
}

export interface UpOpts {
  env: string;
  box?: string | undefined;
  build: boolean;
}

export async function cmdUp(opts: UpOpts): Promise<void> {
  const roles = loadRoles(EXAMPLE_DIR, opts.env);
  const boxes = loadBundles(roles);
  for (const box of pickBox(boxes, opts.box)) {
    console.log(`up ${box.name} (${roles.topology})`);
    await driverFor(roles, box).up(box, { build: opts.build });
  }
  console.log(`kernel: ${hostKernelUrl(roles)}`);
}

export interface DownOpts {
  env: string;
  box?: string | undefined;
  volumes: boolean;
}

export async function cmdDown(opts: DownOpts): Promise<void> {
  const roles = loadRoles(EXAMPLE_DIR, opts.env);
  const result = await render(roles);
  for (const box of pickBox(result.boxes, opts.box)) {
    console.log(`down ${box.name}`);
    await driverFor(roles, box).down(box, { volumes: opts.volumes });
  }
  // Kernel-provisioned workers are socket-born (the launcher owns them);
  // compose never owns them, so sweep them here (referencectl law).
  await sweepManaged();
}

export async function cmdLogs(opts: { env: string; box: string }): Promise<void> {
  const roles = loadRoles(EXAMPLE_DIR, opts.env);
  const [box] = pickBox(loadBundles(roles), opts.box);
  if (box === undefined) fail(`no box "${opts.box}"`);
  await driverFor(roles, box).logs(box);
}
