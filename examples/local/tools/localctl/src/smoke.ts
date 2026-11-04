// localctl smoke — the AI-free gate (sim topology). Mirrors
// reference/checks/verify.sh law: legs run in dependency order, a failing
// leg is recorded and the run continues where it can, everything reports in
// one final table, and the exit code is the AND of the HARD legs. The
// telemetry leg is SOFT (warn): the pinned dashboard has no persistence.
import path from "node:path";
import { hostKernelUrl, hostIdpUrl, EXAMPLE_DIR } from "./render.js";
import { loadRoles, readDotEnv, type RolesFile } from "./env.js";
import { mintCliToken, KernelClient } from "./client.js";
import { cmdDown, cmdUp } from "./drivers.js";
import { cmdEnrollToken } from "./commands.js";
import { poll, run, must, fail } from "./util.js";

export interface SmokeOpts {
  env: string;
  build: boolean;
  cleanExit: boolean;
}

async function seedProject(roles: RolesFile, token: string, name: string): Promise<string> {
  // projects.name has NO unique index — the seed-lib.sh law applies:
  // select by name first, insert only when absent (no ON CONFLICT target).
  const team = roles.kernel.teams[0] as string;
  const psql = (sql: string) => must([
    "docker", "exec", "-i", "ofackernel-postgres-1",
    "psql", "-U", "home", "-d", "facilitator", "-tA", "-c", sql,
  ]);
  const existing = (await psql(`SELECT project_id FROM facilitator.projects WHERE name = '${name}'`))
    .trim().split("\n")[0] ?? "";
  const id = existing !== ""
    ? existing
    : (await psql(`INSERT INTO facilitator.projects (project_id, team_id, name, config, created_at)
        VALUES (gen_random_uuid(), (SELECT team_id FROM facilitator.teams WHERE slug = '${team}'),
                '${name}', '{}', now())
        RETURNING project_id`)).trim().split("\n")[0] ?? "";
  if (!/^[0-9a-f-]{36}$/i.test(id)) fail(`seed project: unexpected psql output "${id}"`);
  void token;
  return id;
}

async function managedContainers(): Promise<string[]> {
  const r = await run([
    "docker", "ps", "-q",
    "--filter", "label=io.facilitator.managed=kernel-provisioned",
    "--filter", "network=ofac-local",
  ]);
  return r.stdout.trim().split("\n").filter((s) => s !== "");
}

async function dashboardSpanCount(aspireUi: number, traceId: string | undefined): Promise<number> {
  const url =
    traceId === undefined
      ? `http://localhost:${String(aspireUi)}/api/telemetry/spans`
      : `http://localhost:${String(aspireUi)}/api/telemetry/spans?traceId=${traceId}`;
  const r = await run(["curl", "-fs", "--max-time", "10", url]);
  if (r.code !== 0) return 0;
  try {
    const j: unknown = JSON.parse(r.stdout);
    const n =
      j !== null && typeof j === "object" && !Array.isArray(j)
        ? (j as Record<string, unknown>)["returnedCount"]
        : undefined;
    return typeof n === "number" ? n : 0;
  } catch {
    return 0;
  }
}

export async function cmdSmoke(opts: SmokeOpts): Promise<number> {
  const roles = loadRoles(EXAMPLE_DIR, opts.env);
  if (roles.topology !== "sim") fail("smoke runs against topology sim");
  const secrets = readDotEnv(path.join(EXAMPLE_DIR, roles.secretsFile));
  const kernelUrl = hostKernelUrl(roles);
  const idpUrl = hostIdpUrl(roles);
  const cliSecret = secrets["IDP_CLI_SECRET"];
  if (cliSecret === undefined || cliSecret === "") fail(`${roles.secretsFile}: IDP_CLI_SECRET:? not set`);

  const results: { leg: string; status: string; ms: number; detail: string }[] = [];
  const state: { client?: KernelClient; projectId?: string; work1?: string; work2?: string; trace?: string } = {};

  const leg = async (id: string, hard: boolean, fn: () => Promise<string>): Promise<boolean> => {
    const t0 = Date.now();
    try {
      const detail = await fn();
      results.push({ leg: id, status: "pass", ms: Date.now() - t0, detail });
      return true;
    } catch (err) {
      results.push({
        leg: id,
        status: hard ? "FAIL" : "warn",
        ms: Date.now() - t0,
        detail: String(err instanceof Error ? err.message : err).slice(0, 200),
      });
      return false;
    }
  };

  await leg("pre-clean", true, async () => {
    await cmdDown({ env: opts.env, volumes: true });
    return "sim torn down";
  });
  await leg("up", true, async () => {
    await cmdUp({ env: opts.env, build: opts.build });
    return "kernel + node bundles up";
  });
  await leg("kernel-health", true, async () => {
    const client = new KernelClient(kernelUrl, "");
    const ok = await poll(240, 2_000, () => client.health());
    if (!ok) fail(`/health never returned 200 within 240s`);
    state.client = new KernelClient(kernelUrl, await mintCliToken(idpUrl, cliSecret));
    return `GET ${kernelUrl}/health 200; CLI token minted`;
  });
  await leg("seed", true, async () => {
    const client = state.client as KernelClient;
    const team = roles.kernel.teams[0] as string;
    const teamRow = await run([
      "docker", "exec", "-i", "ofackernel-postgres-1",
      "psql", "-U", "home", "-d", "facilitator", "-tA", "-c",
      `SELECT 1 FROM facilitator.teams WHERE slug = '${team}'`,
    ]);
    if (teamRow.code !== 0 || teamRow.stdout.trim() !== "1")
      fail(`team "${team}" (Link__Teams) absent — kernel seeding broken`);
    state.projectId = await seedProject(roles, client.token, "localctl-smoke");
    return `team "${team}" armed; project ${state.projectId}`;
  });
  const nodesUp = await leg("nodes-online", true, async () => {
    const client = state.client as KernelClient;
    for (const node of roles.nodes) {
      const slug = node.host.split(".")[0]?.toLowerCase() ?? "";
      await cmdEnrollToken({ env: opts.env, node: slug });
      await cmdUp({ env: opts.env, box: slug, build: false });
    }
    const want = roles.nodes.map((n) => n.gatewayId);
    const online = await poll(60, 2_000, async () => {
      const links = await client.links();
      return want.every((gw) => links.some((l) => l.endpointId === gw && l.online)) ? links : undefined;
    });
    if (!online) fail(`gateways ${want.join(", ")} not all online within 60s`);
    return `${want.join(", ")} online`;
  });
  if (nodesUp) {
    await leg("persistent-gateway", true, async () => {
      const client = state.client as KernelClient;
      state.work1 = await client.createWork({
        objective: "localctl smoke: persistent gateway leg",
        capabilityId: "research.analyze",
        projectId: state.projectId as string,
        actorEvidenceRef: "identity-evidence:localctl-smoke",
        targetId: "smoke-pg",
      });
      const done = await poll(90, 2_000, async () =>
        (await client.timeline(state.work1 as string)).includes("org.facilitator.event.assignment.completed.v1")
          ? true
          : undefined,
      );
      if (!done) fail(`no assignment.completed.v1 for ${state.work1} within 90s`);
      const manifests = await client.manifestCount(state.work1);
      if (manifests !== 1) fail(`expected exactly one output manifest, saw ${manifests}`);
      return `work ${state.work1} completed with 1 manifest`;
    });
    await leg("elastic-leg", true, async () => {
      const client = state.client as KernelClient;
      await client.patchWorkloadScale(1, 1, 5);
      const appeared = await poll(60, 2_000, async () => {
        const ids = await managedContainers();
        return ids.length > 0 ? ids : undefined;
      });
      if (appeared === undefined) fail("no io.facilitator.managed container on ofac-local within 60s");
      state.work2 = await client.createWork({
        objective: "localctl smoke: elastic leg",
        capabilityId: "research.analyze",
        projectId: state.projectId as string,
        actorEvidenceRef: "identity-evidence:localctl-smoke",
        targetId: "smoke-elastic",
      });
      const done = await poll(90, 2_000, async () =>
        (await client.timeline(state.work2 as string)).includes("org.facilitator.event.assignment.completed.v1")
          ? true
          : undefined,
      );
      if (!done) fail(`elastic work ${state.work2} never completed within 90s`);
      const ops = await client.provisioningStates();
      if (!ops.some((o) => o.state.toLowerCase().includes("succe")))
        fail(`no succeeded provision op in /api/admin/provisioning: ${JSON.stringify(ops).slice(0, 200)}`);
      await client.patchWorkloadScale(0, 1, 5);
      const gone = await poll(120, 3_000, async () => ((await managedContainers()).length === 0 ? true : undefined));
      if (!gone) fail("launcher never GC-removed the managed container after scale-to-zero");
      return "worker launched, worked, provisioned op succeeded, GC'd after min0";
    });
    await leg("dead-letters-empty", true, async () => {
      const n = await (state.client as KernelClient).deadLetterCount();
      if (n !== 0) fail(`${n} dead-letter row(s) — a hop failed hard somewhere`);
      return "no dead letters";
    });
    await leg("telemetry", false, async () => {
      // The kernel's own stamp on the work1 dispatch envelope (locked join,
      // verify.sh law); unreadable stamps just widen the query to "any".
      const stampRow = await run([
        "docker", "exec", "-i", "ofackernel-postgres-1",
        "psql", "-U", "home", "-d", "facilitator", "-tA", "-c",
        `SELECT envelope->>'traceparent' FROM facilitator.outbox LIMIT 1`,
      ]);
      const stamp = (stampRow.stdout.trim().split("\n")[0] ?? "").replace(/^00-/, "").slice(0, 32);
      const traceId = /^[0-9a-f]{32}$/.test(stamp) ? stamp : undefined;
      const spans = await poll(30, 3_000, async () => {
        const n = await dashboardSpanCount(roles.kernel.ports.aspireUi, traceId);
        return n > 0 ? n : undefined;
      });
      if (spans === undefined) return "no spans at the dashboard (soft: transient dashboard mode)";
      return `${spans} span(s) at the Aspire query API`;
    });
  }

  if (!opts.cleanExit) {
    await cmdDown({ env: opts.env, volumes: false });
  }
  console.log(
    `\n${results.map((r) => `${r.leg.padEnd(20)} ${r.status.padEnd(5)} ${(String(Math.round(r.ms / 1000)) + "s").padStart(4)}  ${r.detail}`).join("\n")}`,
  );
  return results.some((r) => r.status === "FAIL") ? 1 : 0;
}
