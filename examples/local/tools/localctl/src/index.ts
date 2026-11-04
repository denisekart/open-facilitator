// localctl — the single entrypoint for the local home-deployment example.
// Roles file + secrets + versions.env in, rendered bundles + running
import { render, hostKernelUrl, hostIdpUrl, EXAMPLE_DIR } from "./render.js";
import { loadRoles } from "./env.js";
import { cmdInit, cmdEnrollToken, cmdEnroll, cmdStatus, cmdBackup, cmdUpgrade } from "./commands.js";
import { cmdUp, cmdDown, cmdLogs } from "./drivers.js";
import { cmdSmoke } from "./smoke.js";
import { fail } from "./util.js";

interface Flags {
  env: string;
  node?: string | undefined;
  box?: string | undefined;
  build: boolean;
  volumes: boolean;
  cleanExit: boolean;
}

function parseFlags(args: string[]): Flags {
  const flags: Flags = { env: "sim", build: true, volumes: false, cleanExit: false };
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    if (a === "--env") flags.env = args[++i] ?? fail("--env needs a value");
    else if (a === "--node") flags.node = args[++i] ?? fail("--node needs a value");
    else if (a === "--box") flags.box = args[++i] ?? fail("--box needs a value");
    else if (a === "--no-build") flags.build = false;
    else if (a === "-v" || a === "--volumes") flags.volumes = true;
    else if (a === "--clean-exit") flags.cleanExit = true;
    else fail(`localctl: unknown flag "${a ?? ""}"`);
  }
  return flags;
}

const USAGE = `localctl <verb> [--env NAME] [...]
  init                       generate the env's secrets file
  render                     render per-box bundles + lock (no docker)
  up [box] [--no-build]      start boxes (sim: local compose projects)
  down [box] [-v]            stop boxes, sweep managed workers (-v drops volumes)
  enroll-token --node NAME   mint a gateway token, write it into the bundle
  enroll NAME                enroll-token + up
  status                     per-box ps + link table
  logs --box NAME            tail container logs
  backup                     pg_dump + artifacts mirror + volume tars
  upgrade [box]              re-resolve digests, roll boxes
  smoke [--env sim]          the full AI-free gate (sim)`;

async function main(): Promise<number> {
  const [verb, ...rest] = process.argv.slice(2);
  const flags = parseFlags(rest);
  const positional: string[] = [];
  for (let i = 0; i < rest.length; i++) {
    const a = rest[i] as string;
    if (["--env", "--node", "--box"].includes(a)) {
      i++; // its value
    } else if (!a.startsWith("-")) {
      positional.push(a);
    }
  }
  switch (verb) {
    case "init":
      await cmdInit({ env: flags.env });
      return 0;
    case "render": {
      const roles = loadRoles(EXAMPLE_DIR, flags.env);
      const result = await render(roles);
      console.log(`rendered ${result.boxes.length} boxes -> ${result.outDir}`);
      console.log(`kernel ${hostKernelUrl(roles)}  idp ${hostIdpUrl(roles)}`);
      return 0;
    }
    case "up":
      await cmdUp({ env: flags.env, build: flags.build, ...(positional[0] === undefined ? {} : { box: positional[0] }) });
      return 0;
    case "down":
      await cmdDown({ env: flags.env, volumes: flags.volumes, ...(positional[0] === undefined ? {} : { box: positional[0] }) });
      return 0;
    case "enroll-token":
      if (flags.node === undefined) fail("enroll-token needs --node NAME");
      await cmdEnrollToken({ env: flags.env, node: flags.node });
      return 0;
    case "enroll": {
      const node = positional[0] ?? flags.node ?? fail("enroll needs a node name");
      await cmdEnroll({ env: flags.env, node, build: flags.build });
      return 0;
    }
    case "status":
      await cmdStatus({ env: flags.env });
      return 0;
    case "logs":
      await cmdLogs({ env: flags.env, box: flags.box ?? positional[0] ?? fail("logs needs a box name") });
      return 0;
    case "backup":
      await cmdBackup({ env: flags.env });
      return 0;
    case "upgrade":
      await cmdUpgrade({ env: flags.env, ...(flags.box === undefined && positional[0] === undefined ? {} : { box: flags.box ?? positional[0] }) });
      return 0;
    case "smoke":
      return await cmdSmoke({ env: flags.env, build: flags.build, cleanExit: flags.cleanExit });
    default:
      console.log(USAGE);
      return verb === undefined ? 0 : 2;
  }
}

main().then(
  (code) => {
    process.exitCode = code;
  },
  (err) => {
    console.error(`localctl: ${String(err instanceof Error ? err.stack ?? err.message : err)}`);
    process.exitCode = 1;
  },
);


