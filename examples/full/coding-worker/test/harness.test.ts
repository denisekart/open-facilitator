// The coding harness driven in-process against a REAL local git remote
// (`git init --bare` in a tmpdir — no compose, no docker): the steer window
// (before-complete incorporates, after-terminal is an honest no-op), the
// reconcile law (a re-dispatch updates, never duplicates, the changelog),
// and the push-conflict path (a pre-existing branch is adopted, the
// force-with-lease push lands the worker's commit, no merge).
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { validateAgainstSchemaId } from "@ofac/protocol";
import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import { createMemoryLogger } from "@ofac/workspace-link";
import { startStubKernel, type StubKernel } from "@ofac/adapter/testing";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import type { RunContext } from "@ofac/adapter";
import { createCodingHooks, shortId, type CodingStructuredResult } from "../src/harness.js";
import { runTool } from "../src/git.js";

const COMPLETED_PAYLOAD_ID =
  "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json";
const COMPLETED_TYPE = "org.facilitator.event.assignment.completed.v1";

let kernel: StubKernel;

beforeAll(async () => {
  kernel = await startStubKernel();
});

afterAll(async () => {
  await kernel.close();
});

// --- the remote: a bare repo seeded with main + README -------------------
const REMOTE = mkdtempSync(join(tmpdir(), "ofac-cw-remote-"));
const REMOTE_URL = join(REMOTE, "widgets.git");

beforeAll(async () => {
  await runTool("git", ["init", "--bare", REMOTE_URL]);
  // The default branch of the seeded repo is main — every clone checks it out.
  await runTool("git", ["symbolic-ref", "HEAD", "refs/heads/main"], { cwd: REMOTE_URL });
  const seed = join(REMOTE, "seed");
  await runTool("git", ["clone", REMOTE_URL, seed]);
  writeFileSync(join(seed, "README.md"), "# Widgets\n");
  await runTool("git", ["add", "-A"], { cwd: seed });
  await runTool(
    "git",
    ["-c", "user.name=seeder", "-c", "user.email=seed@test", "commit", "-m", "seed"],
    { cwd: seed },
  );
  await runTool("git", ["push", "-u", "origin", "main"], { cwd: seed });
});

interface FakeCtx {
  ctx: RunContext;
  events: EnvelopeEvent[];
  progress: string[];
  controller: AbortController;
}

function assignCommand(
  workId: string,
  assignmentId: string,
  inputs: Record<string, unknown>,
): EnvelopeCommand {
  return {
    specversion: "1.0",
    id: `env-${workId}`,
    source: "urn:facilitator:instance:t",
    type: "org.facilitator.command.work.assign.v1",
    subject: `work/${workId}/assignment/${assignmentId}`,
    time: "2036-10-23T18:00:00Z",
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
    traceparent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
    data: {
      commandId: `cmd-${workId}`,
      actorEvidenceRef: "identity-evidence:t",
      workId,
      targetId: "gw.coding/coding-worker",
      capabilityId: "coding.implement",
      expectedContextVersion: 1,
      inputs: { objective: `objective ${workId}`, sourceRefs: [], ...inputs },
      expectedDeliverables: [{ kind: "coding.change", mediaType: "text/markdown" }],
      deadline: "2036-10-23T18:00:00Z",
      assignmentId,
      childWorkspaceId: "gw.coding/coding-worker",
    },
  } as EnvelopeCommand;
}

function fakeContext(
  executionId: string,
  workId: string,
  assignmentId: string,
  inputs: Record<string, unknown>,
): FakeCtx {
  const controller = new AbortController();
  const events: EnvelopeEvent[] = [];
  const progress: string[] = [];
  const ctx: RunContext = {
    executionId,
    executionGeneration: 3,
    command: assignCommand(workId, assignmentId, inputs),
    signal: controller.signal,
    kernel: { baseUrl: kernel.url, token: "child-token" },
    publishEvent: (env) => {
      events.push(env);
    },
    publishProgress: (note) => {
      progress.push(note);
    },
    setSteered: () => undefined,
    // The coding script never asks; a question here is a bug, so fail loudly.
    requestInput: () => Promise.reject(new Error("unexpected requestInput")),
  };
  return { ctx, events, progress, controller };
}

function hooksFor(dir: string) {
  return createCodingHooks({
    childWorkspaceId: "gw.coding/coding-worker",
    workDir: dir,
    forgejoUrl: "http://forgejo.test:3000",
    tokenFile: "/nonexistent/pat",
    authorName: "facilitator-bot",
    authorEmail: "bot@openfacilitator.org",
    logger: createMemoryLogger(),
    // A local bare remote needs no credential: full URL control.
    cloneUrlOf: () => REMOTE_URL,
    readToken: () => "pat-never-used-locally",
  });
}

function workDir(): string {
  return mkdtempSync(join(tmpdir(), "ofac-cw-work-"));
}

// Poll on the observable condition (the progress note), never a guessed sleep.
async function waitFor(what: string, probe: () => boolean, timeoutMs = 10_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!probe()) {
    if (Date.now() > deadline) throw new Error(`timeout waiting for ${what}`);
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

function expectCompleted(events: EnvelopeEvent[]): {
  resultManifestRef: string;
  resultDigest: string;
} {
  const completed = events.find((e) => e.type === COMPLETED_TYPE);
  expect(completed).toBeDefined();
  const data = (completed as EnvelopeEvent).data as Record<string, unknown>;
  expect(
    validateAgainstSchemaId(COMPLETED_PAYLOAD_ID, data).ok,
    "completed payload validates against event.assignment.completed.v1",
  ).toBe(true);
  return {
    resultManifestRef: String(data["resultManifestRef"]),
    resultDigest: String(data["resultDigest"]),
  };
}

async function remoteBranches(): Promise<string[]> {
  const out = await runTool("git", ["ls-remote", "--heads", REMOTE_URL]);
  return out.stdout
    .split("\n")
    .filter((line) => line.trim() !== "")
    .map((line) => String(line.split("\t")[1]));
}

async function remoteHead(branch: string): Promise<string> {
  const out = await runTool("git", ["ls-remote", "--heads", REMOTE_URL, branch]);
  return out.stdout.split(/\s+/)[0] ?? "";
}

function structuredOf(result: CodingStructuredResult | undefined): CodingStructuredResult {
  if (result === undefined) throw new Error("no structuredResult recorded");
  return result;
}

/** Read a file off a remote branch via a throwaway clone. */
async function readRemoteFile(branch: string, file: string): Promise<string> {
  const dir = mkdtempSync(join(tmpdir(), "ofac-cw-read-"));
  await runTool("git", ["clone", "-b", branch, REMOTE_URL, dir]);
  return readFileSync(join(dir, file), "utf8");
}

describe("coding harness over a local bare remote", () => {
  it("steer before complete: one commentRequest + steersIncorporated 1", async () => {
    const dir = workDir();
    const hooks = hooksFor(dir);
    const { ctx, events, progress } = fakeContext("exec-steer", "work-steer", "asg-steer-one", {
      repo: "demo/widgets",
      issue: 7,
      objective: "add a changelog entry",
    });
    const running = hooks.onExecute(ctx);
    // The steer window: while the run is mid-flight (past the clone, at the
    // test gate / push phase) a directive lands BEFORE the completed event.
    await waitFor("a running-phase progress note", () => progress.length >= 2);
    hooks.onSteer("focus on the CLI flags", ctx);
    const outcome = await running;
    expect(outcome).toEqual({ kind: "completed" });

    const structured = structuredOf(hooks.lastStructuredResult());
    expect(structured.steersIncorporated).toBe(1);
    expect(structured.commentRequests?.length).toBe(1);
    expect(structured.commentRequests?.[0]?.body).toContain("focus on the CLI flags");
    expect(structured.commentRequests?.[0]?.issue).toBe(7);
    // First run with a repo: the PR rides the manifest for the kernel to ask.
    expect(structured.prRequest).toBeDefined();
    expect(structured.prRequest?.head).toBe(`ofac/${shortId("asg-steer-one")}`);
    expect(structured.prRequest?.body).toContain("Closes #7");

    const result = expectCompleted(events);
    expect(result.resultManifestRef).toMatch(
      /^artifact:\/\/work-steer\/asg-steer-one\/[0-9a-f-]+$/,
    );
    expect(result.resultDigest).toMatch(/^sha256:[0-9a-f]{64}$/);
    // The push landed the branch on the remote.
    expect(await remoteBranches()).toContain(`refs/heads/ofac/${shortId("asg-steer-one")}`);
  });

  it("steer after terminal: honest no-op, structuredResult unchanged", async () => {
    const dir = workDir();
    const hooks = hooksFor(dir);
    const { ctx, events } = fakeContext("exec-late", "work-late", "asg-late-two", {
      repo: "demo/widgets",
      issue: 9,
    });
    await hooks.onExecute(ctx);
    const before = structuredOf(hooks.lastStructuredResult());
    expect(before.steersIncorporated).toBe(0);
    hooks.onSteer("too late, friend", ctx);
    const after = structuredOf(hooks.lastStructuredResult());
    expect(after).toEqual(before);
    expect(after.commentRequests).toBeUndefined();
    expect(events.filter((e) => e.type === COMPLETED_TYPE).length).toBe(1);
  });

  it("reconcile: a re-dispatch updates the changelog, never duplicates it", async () => {
    const dir = workDir();
    const hooks = hooksFor(dir);
    const first = fakeContext("exec-r1", "work-recon", "asg-reconcile", {
      repo: "demo/widgets",
      issue: 3,
      objective: "changelog polish",
    });
    await hooks.onExecute(first.ctx);
    const branch = `ofac/${shortId("asg-reconcile")}`;
    const headAfterFirst = await remoteHead(branch);

    // Re-dispatch: same assignment, prior-context steers ride inputs.
    const second = fakeContext("exec-r2", "work-recon", "asg-reconcile", {
      repo: "demo/widgets",
      issue: 3,
      objective: "changelog polish",
      steers: ["mention the migration order"],
    });
    await hooks.onExecute(second.ctx);

    const structured = structuredOf(hooks.lastStructuredResult());
    expect(structured.steersIncorporated).toBe(1);
    // No second PR request on a re-dispatch — the PR exists.
    expect(structured.prRequest).toBeUndefined();
    expect(structured.commentRequests?.length).toBe(1);

    const changelog = await readRemoteFile(branch, "CODE_CHANGELOG.md");
    const headers = changelog.split("\n").filter((line) => line.startsWith("## "));
    expect(headers.length).toBe(1);
    expect(headers[0]).toBe(`## ${shortId("asg-reconcile")} changelog polish`);
    expect(changelog).toContain("steer: mention the migration order");
    // The second run advanced the branch from first's tip (not a reset).
    const newHead = await remoteHead(branch);
    expect(newHead).not.toBe(headAfterFirst);
    // The reconcile reused THIS assignment's branch — two executions, one
    // branch (the remote also carries other tests' branches: filter to ours).
    const ours = (await remoteBranches()).filter((r) => r.endsWith(branch));
    expect(ours).toEqual([`refs/heads/${branch}`]);
  });

  it("push conflict: a pre-existing branch is adopted, no merge, worker lands", async () => {
    const dir = workDir();
    const hooks = hooksFor(dir);
    const branch = `ofac/${shortId("asg-conflict")}`;
    // Pre-create the branch on the remote with an unrelated commit.
    const side = join(REMOTE, "side");
    await runTool("git", ["clone", "-b", "main", REMOTE_URL, side]);
    writeFileSync(join(side, "UNRELATED.md"), "stranger\n");
    await runTool("git", ["add", "-A"], { cwd: side });
    await runTool(
      "git",
      ["-c", "user.name=side", "-c", "user.email=side@test", "commit", "-m", "side"],
      { cwd: side },
    );
    await runTool("git", ["push", "origin", `HEAD:refs/heads/${branch}`], { cwd: side });
    const strangerHead = await remoteHead(branch);

    const { ctx } = fakeContext("exec-conflict", "work-conflict", "asg-conflict", {
      repo: "demo/widgets",
      issue: 11,
    });
    await hooks.onExecute(ctx);

    const structured = structuredOf(hooks.lastStructuredResult());
    // The tracked checkout adopted the stranger commit; the lease push is
    // honest and the remote head IS the worker's commit.
    expect(structured.commitSha).toBeDefined();
    expect(await remoteHead(branch)).toBe(structured.commitSha);
    // Linear lineage (one parent — no merge commit), stranger is an ancestor.
    const log = await runTool("git", ["fetch", "origin", `${branch}:${branch}-seen`], {
      cwd: side,
    }).then(() => runTool("git", ["log", "--format=%H %P", "-1", `${branch}-seen`], { cwd: side }));
    expect(log.stdout.startsWith(structured.commitSha as string)).toBe(true);
    expect(log.stdout.trim().split(/\s+/).length).toBe(2);
    const ancestors = await runTool("git", ["rev-list", `${branch}-seen`], { cwd: side });
    expect(ancestors.stdout).toContain(strangerHead);
  });
});
