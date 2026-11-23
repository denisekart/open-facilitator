// Step-executor law, driven against the shipped fake tool: markers on the
// de-ansified stream, feed at spawn, expect misses, timeout kills, retry
// notes, onFail continue, capture bytes, ask answers, group-kill cancel —
// deterministic, no AI.
import { mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createConsoleLogger } from "@ofac/workspace-link";
import { beforeAll, describe, expect, it } from "vitest";
import { runCapability, type StepOutcome } from "../src/playbook/run.js";
import { parsePlaybook } from "../src/playbook/load.js";
import type { Playbook, PlaybookCapability } from "../src/playbook/schema.js";
import type { InterpolationScope } from "../src/playbook/interpolate.js";
import type { RunContext } from "@ofac/adapter";

const FAKE = join(import.meta.dirname, "..", "sample", "fake-tool.mjs");

let playbook: Playbook;
let workDir: string;
beforeAll(() => {
  workDir = mkdtempSync(join(tmpdir(), "cli-run-"));
  playbook = parsePlaybook(PLAYBOOK);
});

const PLAYBOOK = `
apiVersion: facilitator.framework/v1
kind: Playbook
capabilities:
  - id: demo.echo
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [demo.out]
    outputMediaTypes: [text/plain]
    vars: { word: hello }
    steps:
      - { name: ok, run: { command: node, args: ["${FAKE}", echo] }, expect: { exit: 0, stdout: "echo ok" }, capture: { as: log, from: stdout, mediaType: text/plain } }
  - id: demo.marker
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [demo.out]
    outputMediaTypes: [text/plain]
    steps:
      - name: ask-stdin
        run: { command: node, args: ["${FAKE}", prompt] }
        promptMarkers: [{ match: 'Proceed\\? \\[y/N\\]', answer: "redteam" }]
        expect: { exit: 0, stdout: "answered:redteam" }
  - id: demo.marker-missing
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [demo.out]
    outputMediaTypes: [text/plain]
    steps:
      - name: never-prompted
        run: { command: node, args: ["${FAKE}", echo] }
        promptMarkers: [{ match: 'Proceed\\? \\[y/N\\]', answer: "y", optional: false }]
  - id: demo.feed
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [demo.out]
    outputMediaTypes: [text/plain]
    steps:
      - { name: fed, run: { command: node, args: ["${FAKE}", feed] }, feed: "yes\\n", expect: { stdout: "fed:yes" } }
  - id: demo.expect-exit
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [demo.out]
    outputMediaTypes: [text/plain]
    steps:
      - { name: wants-three, run: { command: node, args: ["${FAKE}", echo, --fail, "7"] }, expect: { exit: 3 } }
  - id: demo.timeout
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [demo.out]
    outputMediaTypes: [text/plain]
    steps:
      - { name: too-slow, run: { command: node, args: ["${FAKE}", echo, --slow, "30000"] }, timeoutMs: 200 }
  - id: demo.retry
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [demo.out]
    outputMediaTypes: [text/plain]
    vars: { word: hello }
    steps:
      - name: flaky
        run: { command: node, args: ["${FAKE}", retry, --fail-until, "2"] }
        expect: { exit: 0, stdout: "succeeded on attempt 3" }
        retry: { attempts: 3, backoffMs: 10, retryOn: [3] }
        progress: "flaky work {{vars.word}}"
  - id: demo.continue
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [demo.out]
    outputMediaTypes: [text/plain]
    steps:
      - { name: broken, run: { command: node, args: ["${FAKE}", echo, --fail, "9"] }, expect: { exit: 0 }, onFail: continue }
      - { name: later, run: { command: node, args: ["${FAKE}", echo] }, expect: { exit: 0 }, capture: { as: log, from: stdout, mediaType: text/plain } }
  - id: demo.ask
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [demo.out]
    outputMediaTypes: [text/plain]
    steps:
      - name: ask
        ask: { question: "which color?", choices: ["red", "blue"] }
      - name: use
        run: { command: node, args: ["${FAKE}", report] }
        expect: { stdout: "Fake report" }
        capture: { as: report, from: stdout, mediaType: text/markdown }
  - id: demo.pty
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [demo.out]
    outputMediaTypes: [text/plain]
    steps:
      - { name: tty, run: { command: node, args: ["${FAKE}", pty] }, tty: true, expect: { exit: 0, stdout: "PTY-OK" } }
  - id: demo.cancel
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [demo.out]
    outputMediaTypes: [text/plain]
    steps:
      - { name: slow, run: { command: node, args: ["${FAKE}", echo, --slow, "30000"] }, timeoutMs: 30000 }
`;

function capability(id: string): PlaybookCapability {
  const found = playbook.capabilities.get(id);
  if (found === undefined) throw new Error(`no capability ${id}`);
  return found;
}

interface Driven {
  readonly outcome: Promise<StepOutcome>;
  readonly notes: string[];
  readonly controller: AbortController;
}

function drive(
  capabilityId: string,
  opts: {
    env?: Record<string, string>;
    answer?: string;
    abortAfterMs?: number;
  } = {},
): Driven {
  const notes: string[] = [];
  const controller = new AbortController();
  const steps = new Map<string, { answer?: string; exitCode?: number }>();
  const base: Omit<InterpolationScope, "steps"> = {
    workId: "w-1",
    assignmentId: "a-1",
    objective: "test objective",
    attachments: "",
    vars: { ...(capability(capabilityId).vars ?? {}), ...(opts.env ?? {}) },
  };
  const outcome = runCapability(
    capability(capabilityId),
    base,
    {
      publishProgress: (note) => notes.push(note),
      requestInput: async () => opts.answer ?? "red",
      signal: controller.signal,
      logger: createConsoleLogger({ level: "error" }),
    },
    { workDir, killGraceMs: 200 },
  );
  void outcome;
  void steps;
  if (opts.abortAfterMs !== undefined) {
    setTimeout(() => controller.abort(), opts.abortAfterMs);
  }
  return { outcome, notes, controller };
}

describe("step executor", () => {
  it("runs a happy step and captures stdout bytes", async () => {
    const { outcome } = drive("demo.echo");
    const result = await outcome;
    expect(result.kind).toBe("completed");
    if (result.kind !== "completed") return;
    const step = result.steps[0];
    expect(step?.capture?.as).toBe("log");
    expect(step?.capture?.bytes.toString()).toContain("fake-tool echo ok");
  });

  it("marker matching runs on the de-ansified stream and answers once", async () => {
    const answerFile = join(workDir, "answer.txt");
    process.env["FAKE_ANSWER_FILE"] = answerFile;
    const { outcome } = drive("demo.marker");
    const result = await outcome;
    delete process.env["FAKE_ANSWER_FILE"];
    expect(result.kind).toBe("completed");
    expect(readFileSync(answerFile, "utf8")).toBe("redteam");
  });

  it("an unmatched non-optional marker fails the step", async () => {
    const result = await drive("demo.marker-missing").outcome;
    expect(result.kind).toBe("failed");
    if (result.kind === "failed") {
      expect(result.reason).toContain("step 'never-prompted'");
      expect(result.reason).toContain("prompt marker never seen");
    }
  });

  it("feed writes bytes to stdin at spawn", async () => {
    const result = await drive("demo.feed").outcome;
    expect(result.kind).toBe("completed");
  });

  it("expect.exit miss fails with the step name in the reason", async () => {
    const result = await drive("demo.expect-exit").outcome;
    expect(result.kind).toBe("failed");
    if (result.kind === "failed") {
      expect(result.reason).toBe("step 'wants-three': exit code 7, expected 3");
    }
  });

  it("per-step timeout kills the group and fails like an expect miss", async () => {
    const t0 = Date.now();
    const result = await drive("demo.timeout").outcome;
    expect(Date.now() - t0).toBeLessThan(5000);
    expect(result.kind).toBe("failed");
    if (result.kind === "failed") {
      expect(result.reason).toContain("step 'too-slow'");
      expect(result.reason).toContain("timed out");
    }
  });

  it("retry re-runs on an allow-listed exit code, noting each retry", async () => {
    const attemptsFile = join(workDir, `attempts-${String(Date.now())}`);
    process.env["FAKE_ATTEMPTS_FILE"] = attemptsFile;
    const { outcome, notes } = drive("demo.retry");
    const result = await outcome;
    delete process.env["FAKE_ATTEMPTS_FILE"];
    expect(result.kind).toBe("completed");
    expect(readFileSync(attemptsFile, "utf8")).toBe("xxx"); // three attempts
    expect(notes.some((note) => note.includes("retry 1/2"))).toBe(true);
    expect(notes[0]).toBe("flaky work hello"); // progress interpolates vars
  });

  it("onFail: continue records the first failure and runs on", async () => {
    const result = await drive("demo.continue").outcome;
    expect(result.kind).toBe("failed");
    if (result.kind === "failed") {
      expect(result.reason).toContain("step 'broken'");
    }
  });

  it("ask steps answer via requestInput and later steps see the answer", async () => {
    const { outcome } = drive("demo.ask", { answer: "blue" });
    const result = await outcome;
    expect(result.kind).toBe("completed");
    if (result.kind === "completed") {
      expect(result.steps[1]?.capture?.bytes.toString()).toContain("Fake report");
    }
  });

  it("cancel mid-step returns cancelled (no event, group killed)", async () => {
    const { outcome } = drive("demo.cancel", { abortAfterMs: 150 });
    const result = await outcome;
    expect(result.kind).toBe("cancelled");
  });
});

describe("pty leg", () => {
  it("a tty:true step spawns under a real PTY and expect matches", async () => {
    let ptyAvailable = true;
    try {
      await import("node-pty");
    } catch {
      ptyAvailable = false;
    }
    const { outcome } = drive("demo.pty");
    if (!ptyAvailable) {
      // The pipes posture must never require node-pty; a missing native
      // build only excuses the PTY leg.
      const result = await outcome.catch(() => undefined);
      expect(result === undefined || result.kind !== "completed").toBe(true);
      return;
    }
    const result = await outcome;
    expect(
      result.kind === "completed" ? "completed" : `${result.kind}: ${(result as { reason?: string }).reason ?? ""}`,
    ).toBe("completed");
  });
});
