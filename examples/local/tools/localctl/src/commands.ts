// The operator verbs: init (secrets), enroll-token / enroll (the minted
// hand-off), status, backup, upgrade. Every leg names what failed; nothing
// here guesses a default the roles file should have carried.
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { randomBytes } from "node:crypto";
import { loadBundles, hostIdpUrl, hostKernelUrl, render, EXAMPLE_DIR, type RenderedBox } from "./render.js";
import { loadRoles, readDotEnv, type RolesFile } from "./env.js";
import { loadLock, type ImageKey } from "./lock.js";
import { mintCliToken, KernelClient } from "./client.js";
import { run, must, fail, table } from "./util.js";
import { cmdUp, driverFor } from "./drivers.js";

const SECRET_KEYS = [
  "OFAC_ENROLLMENT_TOKEN",
  "POSTGRES_PASSWORD",
  "S3_ACCESS_KEY",
  "S3_SECRET_KEY",
  "IDP_CLI_SECRET",
  "KEYCLOAK_ADMIN_PASSWORD",
];

/** localctl init: generate the secrets file (openssl-strength hex, node
 * crypto so no subprocess is needed — same bytes as `openssl rand -hex 16`).
 * Existing files are NEVER touched (generate-secrets.sh law). */
export async function cmdInit(opts: { env: string }): Promise<void> {
  const roles = loadRoles(EXAMPLE_DIR, opts.env);
  const file = path.join(EXAMPLE_DIR, roles.secretsFile);
  if (existsSync(file)) {
    console.log(`${file}: exists — left untouched`);
    return;
  }
  mkdirSync(path.dirname(file), { recursive: true });
  const lines = [
    "# localctl init — one secret per line; rerun init after deleting.",
    ...SECRET_KEYS.map((k) =>
      k === "S3_ACCESS_KEY" ? `${k}=ofac-admin` : `${k}=${randomBytes(16).toString("hex")}`,
    ),
    "# Per-team gateway-child secrets (kernel LinkChildSecrets law): one per kernel team.",
    ...roles.kernel.teams.map((t) => `CHILD_SECRETS_${t.toUpperCase().replace(/[^A-Z0-9]/g, "_")}=${randomBytes(16).toString("hex")}`),
  ];
  writeFileSync(file, `${lines.join("\n")}\n`, { mode: 0o600 });
  console.log(`${file}: generated`);
}

/** Write OFAC_ENROLL_SECRET into a rendered node bundle's .env (or append). */
export function writeBundleSecret(box: RenderedBox, token: string): void {
  const file = box.env.file;
  const lines = readFileSync(file, "utf8").split("\n");
  const next = lines.map((l) => (l.startsWith("OFAC_ENROLL_SECRET=") ? `OFAC_ENROLL_SECRET=${token}` : l));
  if (!next.some((l) => l.startsWith("OFAC_ENROLL_SECRET="))) next.push(`OFAC_ENROLL_SECRET=${token}`);
  writeFileSync(file, next.join("\n"));
}

async function adminClient(roles: RolesFile): Promise<KernelClient> {
  const secrets = readDotEnv(path.join(EXAMPLE_DIR, roles.secretsFile));
  const secret = secrets["IDP_CLI_SECRET"];
  if (secret === undefined || secret === "") fail(`${roles.secretsFile}: IDP_CLI_SECRET:? not set`);
  const token = await mintCliToken(hostIdpUrl(roles), secret);
  return new KernelClient(hostKernelUrl(roles), token);
}

/** localctl enroll-token --node NAME: mint a gateway enrollment token for
 * the node's team and write it into the node bundle's .env. */
export async function cmdEnrollToken(opts: { env: string; node: string }): Promise<void> {
  const roles = loadRoles(EXAMPLE_DIR, opts.env);
  const box = loadBundles(roles).find((b) => b.name === opts.node);
  const node = roles.nodes.find((n) => n.host.split(".")[0]?.toLowerCase() === opts.node);
  if (box === undefined || node === undefined) fail(`no node "${opts.node}"`);
  const client = await adminClient(roles);
  const minted = await client.mintToken({
    label: `localctl ${roles.name}/${opts.node}`,
    team: node.teams[0] as string,
    role: "gateway",
    ttlMinutes: roles.kernel.enrollmentTtlMinutes,
  });
  writeBundleSecret(box, minted.token);
  console.log(`enrollment token for ${opts.node}: ${minted.id} (expires ${minted.expiresAt}) — written into ${box.env.file}`);
}

/** localctl enroll NODE: token + up in one step (bring-up order). */
export async function cmdEnroll(opts: { env: string; node: string; build: boolean }): Promise<void> {
  await cmdEnrollToken(opts);
  await cmdUp({ env: opts.env, box: opts.node, build: opts.build });
}

/** localctl status: per-box compose ps + kernel links table. */
export async function cmdStatus(opts: { env: string }): Promise<void> {
  const roles = loadRoles(EXAMPLE_DIR, opts.env);
  const boxes = loadBundles(roles);
  for (const box of boxes) {
    try {
      const out = await driverFor(roles, box).ps(box);
      const rows = out
        .split("\n")
        .filter((l) => l.trim() !== "")
        .map((l) => JSON.parse(l) as Record<string, unknown>)
        .map((j) => [
          String(j["Service"] ?? "?"),
          String(j["State"] ?? "?"),
          String(j["Health"] ?? "-"),
        ]);
      console.log(`\n[${box.name}]\n${table(rows, ["service", "state", "health"])}`);
    } catch (err) {
      console.log(`\n[${box.name}] compose ps failed: ${String(err)}`);
    }
  }
  try {
    const client = await adminClient(roles);
    const links = await client.links();
    console.log(
      `\n${table(
        links.map((l) => [l.endpointId, l.team, l.online ? "online" : "offline"]),
        ["endpoint", "team", "link"],
      )}`,
    );
  } catch (err) {
    console.log(`\nlinks unreadable: ${String(err)}`);
  }
  if (roles.topology === "sim")
    console.log('\nnote: add "127.0.0.1 id.localhost" to /etc/hosts for browser logins.');
}

/** localctl backup: pg_dump + mc mirror + aspire/caddy volume tars into a
 * dated dir under the roles backupPath (host folder — survives down -v).
 * Runs against the kernel box (sim: this host). */
export async function cmdBackup(opts: { env: string }): Promise<void> {
  const roles = loadRoles(EXAMPLE_DIR, opts.env);
  const secrets = readDotEnv(path.join(EXAMPLE_DIR, roles.secretsFile));
  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  const dest = path.resolve(EXAMPLE_DIR, roles.backupPath, stamp);
  mkdirSync(dest, { recursive: true });
  const env = {
    POSTGRES_PASSWORD: secrets["POSTGRES_PASSWORD"] ?? "",
    S3_ACCESS_KEY: secrets["S3_ACCESS_KEY"] ?? "",
    S3_SECRET_KEY: secrets["S3_SECRET_KEY"] ?? "",
  };
  await must(
    ["docker", "compose", "-p", "ofackernel", "exec", "-T", "postgres", "pg_dump", "-U", "home", "facilitator"],
    { env: { ...process.env, ...env } as Record<string, string> },
  ).then((dump) => writeFileSync(path.join(dest, "facilitator.sql"), dump));
  await must(
    [
      "docker", "run", "--rm", "--network", "ofac-local",
      "-v", `${dest}:/backup`, "-e", `MINIO_ROOT_USER=${env.S3_ACCESS_KEY}`, "-e", `MINIO_ROOT_PASSWORD=${env.S3_SECRET_KEY}`,
      "minio/mc:latest", "/bin/sh", "-c",
      'mc alias set local http://minio:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" && mc mb --ignore-existing local/backup && mc mirror local/facilitator-artifacts /backup/artifacts',
    ],
    { env: env as Record<string, string> },
  );
  for (const vol of ["ofackernel_aspire_data", "ofackernel_caddy_data"]) {
    const exists = await run(["docker", "volume", "inspect", vol]);
    if (exists.code !== 0) continue; // optional service (sim has no caddy; aspire may run transient)
    await must([
      "docker", "run", "--rm", "-v", `${vol}:/src:ro`, "-v", `${dest}:/backup`,
      "postgres:17-alpine", "tar", "-czf", `/backup/${vol}.tar.gz`, "-C", "/src", ".",
    ]);
  }
  console.log(`backup written: ${dest}`);
}

/** localctl upgrade: re-resolve registry refs (tags -> fresh digests),
 * record the old->new diff in the lock, re-render, roll each box. */
export async function cmdUpgrade(opts: { env: string; box?: string }): Promise<void> {
  const roles = loadRoles(EXAMPLE_DIR, opts.env);
  const before = loadLock(EXAMPLE_DIR, roles.name).resolved;
  await render(roles, { forceResolve: true }); // the render records the diff
  const after = loadLock(EXAMPLE_DIR, roles.name).resolved;
  const diffs: (string | number)[][] = [];
  for (const key of ["kernel", "node", "worker"] as ImageKey[])
    if (before[key] !== after[key]) diffs.push([key, before[key] ?? "unset", after[key] ?? "?"]);
  console.log(diffs.length === 0 ? "upgrade: nothing changed" : `upgrade:\n${table(diffs, ["image", "from", "to"])}`);
  await cmdUp({ env: opts.env, box: opts.box, build: roles.topology === "sim" });
}
