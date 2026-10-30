// The coding harness: a deterministic scripted coder over the SDK hooks.
// One execution = clone (PAT-per-run from the token file, never cached) →
// branch `ofac/<assignment8>` → append CODE_CHANGELOG.md (objective + one
// line per steer, prior-context first) → a deterministic node --test gate →
// commit + push --force-with-lease (a stale branch is a reconcile, first
// writer wins honestly) → complete over the kernel artifact path (Plan 15:
// diff + canonical manifest UPLOADED, completed carries ref + digest).
//
// The PR is the gateway's job (§9.2 — the worker holds no PR API call): the
// manifest's structuredResult carries `prRequest` on a first run and
// `commentRequests[]` per steer, which the kernel's completed-manifest
// reader dispatches as connector ops. Cancel law: an abort mid-run unwinds
// as cancelled — NO terminal event (the accepted stop reply settled the row).
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import type { LinkLogger } from "@ofac/workspace-link";
import {
  artifactUri,
  buildCompletedEvent,
  buildResultManifest,
  createArtifactClient,
  type ResultManifestOutput,
} from "@ofac/adapter";
import type { RunContext, RunOutcome } from "@ofac/adapter";
import {
  checkoutBranch,
  commitAll,
  diffSince,
  gitClone,
  headSha,
  injectToken,
  pushLease,
  runTool,
} from "./git.js";

export interface CodingRunOptions {
  childWorkspaceId: string;
  /** Execution working trees land under <workDir>/<executionId>/. */
  workDir: string;
  /** Forgejo origin, e.g. `http://forgejo:3000` — the clone URL's host part. */
  forgejoUrl: string;
  /** File holding the bot PAT; READ AT EVERY RUN (rotation law). */
  tokenFile: string;
  authorName: string;
  authorEmail: string;
  logger: LinkLogger;
  /** Test seam: skip the git phases (the completed publish still rides). */
  gitless?: boolean | undefined;
  /** Test seam: produce the PAT per call (units); default reads tokenFile. */
  readToken?: (() => string) | undefined;
  /** Test seam: full clone-URL control (units clone a local bare remote by path). */
  cloneUrlOf?: ((repo: string) => string) | undefined;
  /** Injectable fetch for the artifact upload path (tests); default global. */
  fetchImpl?: typeof fetch;
}

export type PrRequest = {
  readonly repo: string;
  readonly head: string;
  readonly base: string;
  readonly title: string;
  readonly body: string;
};

export type CommentRequest = {
  readonly repo: string;
  readonly issue: number | string;
  readonly body: string;
};

// A type ALIAS (not an interface) on purpose: the manifest's
// `structuredResult: Record<string, unknown>` seam accepts aliases with an
// implicit index signature; interfaces are rejected by structural law.
export type CodingStructuredResult = {
  branch: string;
  objective: string;
  steersIncorporated: number;
  /** Absent in gitless mode (no commit was authored). */
  commitSha?: string;
  prRequest?: PrRequest;
  commentRequests?: CommentRequest[];
};

export interface CodingHooks {
  onExecute(ctx: RunContext): Promise<RunOutcome>;
  onSteer(directive: string, ctx: RunContext): void;
  /** The structuredResult of the LAST execution, for units/kits to assert without a kernel. */
  lastStructuredResult(): CodingStructuredResult | undefined;
}

interface RunState {
  /** Prior-context steers (re-dispatch `inputs.steers`) + in-run directives, in order. */
  steers: string[];
  published: boolean;
}

const CHANGELOG = "CODE_CHANGELOG.md";
const TEST_FILE = "tests/ofac-coding-worker.test.mjs";
const TEST_CONTENT = `// Deterministic gate the coding worker authors for its own change: a test
// the run executes before pushing — a change that cannot pass it fails.
import test from "node:test";
import assert from "node:assert/strict";

test("ofac coding worker change is deterministic", () => {
  assert.equal(1 + 1, 2);
});
`;

/** The first 8 alnum chars of an assignment id, lowercase — the branch tail. */
export function shortId(assignmentId: string): string {
  return assignmentId
    .toLowerCase()
    .replace(/[^a-z0-9]/g, "")
    .slice(0, 8);
}

interface AssignFacts {
  workId: string;
  assignmentId: string;
  objective: string;
  repo: string | undefined;
  issue: number | string | undefined;
  priorSteers: string[];
}

/**
 * Append (or reconcile) this run's changelog section. A re-dispatch against
 * a branch that already carries the section appends only the steer lines
 * still missing — the header is written exactly once per assignment.
 */
export function applyChangelog(
  existing: string,
  assignmentShort: string,
  objective: string,
  steers: readonly string[],
): string {
  const header = `## ${assignmentShort} ${objective}`;
  const steerLine = (directive: string): string => `steer: ${directive}`;
  const at = existing.indexOf(header);
  if (at < 0) {
    const lines = [header];
    for (const directive of steers) lines.push(steerLine(directive));
    return `${existing}${lines.join("\n")}\n\n`;
  }
  const sectionEnd = existing.indexOf("\n## ", at + header.length);
  const section = existing.slice(at, sectionEnd < 0 ? existing.length : sectionEnd);
  const missing = steers.filter((directive) => !section.includes(steerLine(directive)));
  if (missing.length === 0) return existing;
  const head = existing.slice(0, at + section.length);
  const trimmed = head.endsWith("\n") ? head : `${head}\n`;
  const tail = sectionEnd < 0 ? "" : existing.slice(at + section.length);
  return `${trimmed}${missing.map(steerLine).join("\n")}\n${tail}`;
}

function readAssign(ctx: RunContext): AssignFacts {
  const data = ctx.command.data as Record<string, unknown>;
  const workId = String(data["workId"] ?? ctx.executionId);
  const assignmentId = String(data["assignmentId"] ?? ctx.executionId);
  const inputs = (data["inputs"] ?? {}) as Record<string, unknown>;
  const objective = typeof inputs["objective"] === "string" ? inputs["objective"] : workId;
  const repo = typeof inputs["repo"] === "string" ? inputs["repo"] : undefined;
  const issue =
    typeof inputs["issue"] === "number" || typeof inputs["issue"] === "string"
      ? inputs["issue"]
      : undefined;
  const priorSteers = Array.isArray(inputs["steers"])
    ? inputs["steers"].filter((s): s is string => typeof s === "string")
    : [];
  return { workId, assignmentId, objective, repo, issue, priorSteers };
}

export function createCodingHooks(options: CodingRunOptions): CodingHooks {
  const { childWorkspaceId, workDir, forgejoUrl, authorName, authorEmail, logger } = options;
  const states = new Map<string, RunState>();
  let lastResult: CodingStructuredResult | undefined;

  async function onExecute(ctx: RunContext): Promise<RunOutcome> {
    const { workId, assignmentId, objective, repo, issue, priorSteers } = readAssign(ctx);
    const state: RunState = { steers: [...priorSteers], published: false };
    states.set(ctx.executionId, state);
    // "First run" law: a re-dispatch carries prior-context steers; only an
    // execution that has never seen this work asks the gateway for a PR.
    const firstRun = priorSteers.length === 0;
    const assignmentShort = shortId(assignmentId);
    const branch = `ofac/${assignmentShort}`;
    ctx.publishProgress(`accepted: coding ${objective} on ${branch}`);
    if (ctx.signal.aborted) return { kind: "completed" };

    let commitSha: string | undefined;
    let diff = "";
    if (options.gitless !== true) {
      if (repo === undefined) {
        throw new Error("coding.implement requires inputs.repo");
      }
      // Per-run PAT read: a rotated token is the next run's truth, and the
      // secret rides argv only — every error text is scrubbed below.
      const pat = options.readToken?.() ?? readFileSync(options.tokenFile, "utf8").trim();
      const cloneUrl =
        options.cloneUrlOf !== undefined
          ? options.cloneUrlOf(repo)
          : injectToken(`${forgejoUrl.replace(/\/+$/, "")}/${repo}.git`, "facilitator-bot", pat);
      const scrub = pat.length > 0 ? [pat] : [];
      mkdirSync(workDir, { recursive: true, mode: 0o700 });
      const repoDir = join(workDir, ctx.executionId);
      await gitClone(cloneUrl, repoDir, scrub);
      ctx.publishProgress(`cloned ${repo}; branch ${branch}`);
      await checkoutBranch(repoDir, branch);

      const existing = existsSync(join(repoDir, CHANGELOG))
        ? readFileSync(join(repoDir, CHANGELOG), "utf8")
        : "";
      writeFileSync(
        join(repoDir, CHANGELOG),
        applyChangelog(existing, assignmentShort, objective, state.steers),
      );
      mkdirSync(join(repoDir, "tests"), { recursive: true });
      writeFileSync(join(repoDir, TEST_FILE), TEST_CONTENT);

      ctx.publishProgress("running the deterministic test gate");
      await runTool("node", ["--test", TEST_FILE], { cwd: repoDir, scrub });

      const base = await headSha(repoDir);
      if (await commitAll(repoDir, authorName, authorEmail, `ofac: ${objective}`)) {
        commitSha = await headSha(repoDir);
        diff = await diffSince(repoDir, base);
      }
      await pushLease(repoDir, branch, scrub);
      ctx.publishProgress(`pushed ${branch}`);
    }
    if (ctx.signal.aborted) return { kind: "completed" };

    // The manifest's structuredResult: the kernel's completed-manifest
    // reader turns prRequest / commentRequests into connector ops; the
    // worker itself calls no PR API (§9.2 — the token is team-side only).
    const structured: CodingStructuredResult = {
      branch,
      objective,
      steersIncorporated: state.steers.length,
      ...(commitSha !== undefined ? { commitSha } : {}),
      ...(firstRun && repo !== undefined
        ? {
            prRequest: {
              repo,
              head: branch,
              base: "main",
              title: objective.slice(0, 72),
              body: `Closes #${issue ?? objective}\n\n[ofac coding worker execution ${ctx.executionId}]`,
            },
          }
        : {}),
      ...(state.steers.length > 0 && repo !== undefined && issue !== undefined
        ? {
            commentRequests: state.steers.map((directive) => ({
              repo,
              issue,
              body: `steer incorporated: ${directive}`,
            })),
          }
        : {}),
    };
    lastResult = structured;

    const createdAt = new Date().toISOString();
    const client = createArtifactClient({
      baseUrl: ctx.kernel.baseUrl,
      token: ctx.kernel.token,
      ...(options.fetchImpl !== undefined ? { fetchImpl: options.fetchImpl } : {}),
    });
    const outputs: ResultManifestOutput[] = [];
    const diffBytes = Buffer.from(diff, "utf8");
    if (commitSha !== undefined && diffBytes.length > 0) {
      const diffArtifact = await client.upload(diffBytes, {
        mediaType: "text/x-diff",
        workId,
        assignmentId,
        retentionPolicy: "30d",
      });
      outputs.push({
        artifactId: diffArtifact.artifactId,
        mediaType: "text/x-diff",
        size: diffBytes.length,
        digest: diffArtifact.digest,
        artifactUri: artifactUri(workId, assignmentId, diffArtifact.artifactId),
        createdBy: `workspace:${childWorkspaceId}`,
        createdAt,
        kind: "coding.change",
        classification: "internal",
      });
    }
    const manifest = buildResultManifest({
      manifestId: `man-${ctx.executionId}`,
      workId,
      assignmentId,
      capability: "coding.implement",
      summary: `Deterministic coding change on ${branch}.`,
      structuredResult: structured,
      ...(commitSha !== undefined ? { externalRefs: [`git://${branch}@${commitSha}`] } : {}),
      outputs,
      createdAt,
    });
    const manifestDoc = await client.upload(manifest.document, {
      mediaType: "application/json",
      workId,
      assignmentId,
      retentionPolicy: "30d",
    });
    const manifestRef = artifactUri(workId, assignmentId, manifestDoc.artifactId);
    state.published = true;
    ctx.publishEvent(
      buildCompletedEvent(
        ctx,
        { manifestId: `man-${ctx.executionId}`, workId, assignmentId },
        { manifestRef, digest: manifest.digest },
      ),
    );
    logger.info("coding-completed", { executionId: ctx.executionId, branch, manifestRef });
    return { kind: "completed" };
  }

  function onSteer(directive: string, ctx: RunContext): void {
    const state = states.get(ctx.executionId);
    // After the run's terminal event the window is closed: honest no-op —
    // the kernel's steer arm re-dispatches with context instead.
    if (state === undefined || state.published) {
      logger.info("coding-steer-ignored", { executionId: ctx.executionId, directive });
      return;
    }
    state.steers.push(directive);
    logger.info("coding-steered", { executionId: ctx.executionId, directive });
  }

  return {
    onExecute,
    onSteer,
    lastStructuredResult: () => lastResult,
  };
}
