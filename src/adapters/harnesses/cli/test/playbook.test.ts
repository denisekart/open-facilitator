// Playbook law matrix: strict validation at every level — an unknown key
// anywhere fails, naming the full dotted path — and the frozen v1 step
// table (run/tty/timeoutMs/expect/promptMarkers/feed/ask/capture/retry/
// onFail/progress). Interpolation only lands in declared slots.
import { describe, expect, it } from "vitest";
import { PlaybookError, parsePlaybook } from "../src/playbook/load.js";
import {
  interpolate,
  validatePlaceholders,
  type InterpolationScope,
} from "../src/playbook/interpolate.js";
import type { Playbook, PlaybookCapability } from "../src/playbook/schema.js";

const BASE = `
apiVersion: facilitator.framework/v1
kind: Playbook
ownerTeam: team-alpha
capabilities:
  - id: research.analyze
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [analysis.report]
    outputMediaTypes: [text/markdown]
    steps:
      - name: analyze
        run: { command: node, args: ["tool.mjs", "{{objective}}"] }
        expect: { exit: 0 }
`;

function parse(yaml: string): Playbook {
  return parsePlaybook(yaml);
}

function expectFail(yaml: string, key: string): void {
  let error: unknown;
  try {
    parse(yaml);
  } catch (caught) {
    error = caught;
  }
  expect(error, `expected failure naming '${key}'`).toBeInstanceOf(PlaybookError);
  expect((error as Error).message).toContain(key);
}

describe("playbook law", () => {
  it("parses the base document and derives the manifest", () => {
    const playbook = parse(BASE);
    const capability = playbook.capabilities.get("research.analyze");
    expect(capability).toBeDefined();
    expect(capability?.steps[0]?.name).toBe("analyze");
    expect(capability?.steps[0]?.run?.command).toBe("node");
    expect(capability?.steps[0]?.timeoutMs).toBe(600_000);
    expect(capability?.steps[0]?.onFail).toBe("abort");
    expect(capability?.steps[0]?.tty).toBe(false);
    expect(playbook.binaries.has("node")).toBe(true);
    expect(playbook.manifest.capabilities).toEqual([
      {
        id: "research.analyze",
        version: 1,
        execution: "asynchronous",
        acceptedContexts: ["uri-reference"],
        outputKinds: ["analysis.report"],
        outputMediaTypes: ["text/markdown"],
        supportsSteering: false,
        supportsCancellation: true,
        supportsNudge: false,
        supportsInteraction: true,
      },
    ]);
  });

  it("defaultTimeoutMs comes from the loader options", () => {
    const playbook = parsePlaybook(BASE, { defaultTimeoutMs: 4200 });
    expect(playbook.capabilities.get("research.analyze")?.steps[0]?.timeoutMs).toBe(4200);
  });

  it("unknown keys rejected by name at every level", () => {
    expectFail(`${BASE}sidecar: true\n`, "file.sidecar: unknown key");
    expectFail(
      BASE.replace(
        "outputKinds: [analysis.report]",
        "outputKinds: [analysis.report]\n    weird: 1",
      ),
      "capabilities[0].weird: unknown key",
    );
    expectFail(
      BASE.replace("expect: { exit: 0 }", "expect: { exit: 0, code: 0 }"),
      "capabilities[0]('research.analyze').steps[0].expect.code: unknown key",
    );
    expectFail(
      BASE.replace(
        'run: { command: node, args: ["tool.mjs", "{{objective}}"] }',
        'run: { command: node, args: [], chdir: "." }',
      ),
      "steps[0].run.chdir: unknown key",
    );
  });

  it("required and shape law: id, execution, steps, regexes", () => {
    expectFail(BASE.replace("id: research.analyze", "id: flat"), "id");
    expectFail(BASE.replace("execution: asynchronous", "execution: synchronous"), "execution");
    expectFail(BASE.replace(/steps:[\s\S]*$/, "steps: []\n"), "steps");
    expectFail(
      BASE.replace("expect: { exit: 0 }", "expect: { exit: 300 }"),
      "expect.exit: must be an integer in 0..255",
    );
    expectFail(
      BASE.replace("expect: { exit: 0 }", 'expect: { stdout: "[unclosed" }'),
      "expect.stdout: not a valid regex",
    );
    expectFail(
      BASE.replace("expect: { exit: 0 }", "promptMarkers:\n        - { match: '[y/N]' }"),
      "promptMarkers[0].answer: must be a string",
    );
  });

  it("step grammar: run and/or ask, unique names, shell:false only", () => {
    expectFail(
      BASE.replace(
        'run: { command: node, args: ["tool.mjs", "{{objective}}"] }\n        expect: { exit: 0 }',
        "progress: thinking",
      ),
      "needs 'run' and/or 'ask'",
    );
    expectFail(
      BASE.replace(
        'run: { command: node, args: ["tool.mjs", "{{objective}}"] }',
        "run: { command: node, args: [], shell: true }",
      ),
      "shell",
    );
    expectFail(
      `${BASE}  - id: research.extra\n    version: 1\n    execution: asynchronous\n    acceptedContexts: [uri-reference]\n    outputKinds: [k]\n    outputMediaTypes: [text/plain]\n    steps:\n      - { name: a, ask: { question: q } }\n      - { name: a, ask: { question: q2 } }\n`,
      "duplicate step name 'a'",
    );
  });

  it("capture/from law: file needs a path, others forbid one", () => {
    expectFail(
      BASE.replace(
        "expect: { exit: 0 }",
        "capture: { as: report, from: file, mediaType: text/markdown }",
      ),
      "capture.path: required for from: file",
    );
    expectFail(
      BASE.replace(
        "expect: { exit: 0 }",
        "capture: { as: report, from: stdout, mediaType: text/markdown, path: x }",
      ),
      "capture.path: only valid with from: file",
    );
  });

  it("env allow-list: keys outside allowedEnvKeys name the key", () => {
    expectFail(
      BASE.replace(
        "acceptedContexts: [uri-reference]",
        "acceptedContexts: [uri-reference]\n    allowedEnvKeys: [HOME]",
      ).replace('args: ["tool.mjs", "{{objective}}"] }', "args: [], env: { TOKEN: x } }"),
      "run.env.TOKEN: not in the capability's allowedEnvKeys",
    );
    const ok = parse(
      BASE.replace(
        "acceptedContexts: [uri-reference]",
        "acceptedContexts: [uri-reference]\n    allowedEnvKeys: [TOKEN]",
      ).replace('args: ["tool.mjs", "{{objective}}"] }', "args: [], env: { TOKEN: x } }"),
    );
    expect(ok.capabilities.get("research.analyze")?.steps[0]?.run?.env).toEqual({ TOKEN: "x" });
  });

  it("when: optional-leg guard; supportsCancellation may demote", () => {
    expectFail(BASE.replace("expect: { exit: 0 }", "when: 42"), "steps[0].when");
    expectFail(
      BASE.replace("expect: { exit: 0 }", "when: Not-A-Var"),
      "steps[0].when: must name a var",
    );
    const pb = parse(
      BASE.replace(
        "acceptedContexts: [uri-reference]",
        "acceptedContexts: [uri-reference]\n    supportsCancellation: false",
      ),
    );
    expect(pb.manifest.capabilities[0]?.supportsCancellation).toBe(false);
    // The manifest face stays honest: still derived, now demoted.
    expect(pb.capabilities.get("research.analyze")?.manifestEntry.supportsSteering).toBe(false);
  });

  it("{{playbookDir}} resolves from the loader options", () => {
    const pb = parsePlaybook(BASE.replace("command: node", 'command: "{{playbookDir}}/node"'), {
      playbookDir: "/opt/pb",
    });
    // A placeholder may not NAME a binary (payload law): it lands in args.
    expect(pb.capabilities.get("research.analyze")?.steps[0]?.run?.command).toBe(
      "{{playbookDir}}/node",
    );
  });

  it("askUserInject: json or toml only", () => {
    expectFail(
      BASE.replace("steps:", "askUserInject: { file: .mcp.json, format: yaml }\n    steps:"),
      "askUserInject.format: must be 'json' or 'toml'",
    );
  });
});

describe("interpolation law", () => {
  const capability = {
    id: "research.analyze",
    vars: { tier: "deep" },
    steps: [{ name: "analyze" }, { name: "ask" }],
  } as unknown as PlaybookCapability;

  function scope(extra: Partial<InterpolationScope> = {}): InterpolationScope {
    return {
      workId: "w-1",
      assignmentId: "a-1",
      objective: "why",
      attachments: "",
      playbookDir: "/pb",
      vars: { tier: "deep" },
      steps: new Map([
        ["ask", { answer: "redteam" }],
        ["analyze", { exitCode: 3 }],
      ]),
      ...extra,
    };
  }

  it("substitutes run state into declared slots only", () => {
    expect(interpolate(scope(), "args[0]", "--objective {{objective}}")).toBe("--objective why");
    expect(interpolate(scope(), "args[0]", "{{playbookDir}}/tool.mjs")).toBe("/pb/tool.mjs");
    expect(interpolate(scope(), "args[0]", "{{workId}}/{{assignmentId}}")).toBe("w-1/a-1");
    expect(interpolate(scope(), "args[0]", "{{vars.tier}}")).toBe("deep");
    expect(interpolate(scope(), "args[0]", "{{steps.ask.answer}}")).toBe("redteam");
    expect(interpolate(scope(), "args[0]", "{{steps.analyze.exitCode}}")).toBe("3");
  });

  it("assignment vars override capability defaults at scope build; missing var fails loud", () => {
    expect(() => interpolate(scope(), "args[0]", "{{vars.nope}}")).toThrow(/vars\.nope/);
    expect(() => interpolate(scope({ vars: {} }), "args[0]", "{{vars.tier}}")).toThrow(
      /vars\.tier/,
    );
  });

  it("unknown or unfinished references fail, never blank", () => {
    expect(() => interpolate(scope(), "args[0]", "{{bogus}}")).toThrow(/unknown placeholder/);
    expect(() => interpolate(scope(), "args[0]", "{{steps.missing.answer}}")).toThrow(
      /unknown or unfinished step/,
    );
    expect(() => interpolate(scope(), "args[0]", "{{steps.analyze.answer}}")).toThrow(
      /recorded no answer/,
    );
    expect(() => interpolate(scope(), "args[0]", "{{shimCommand}}")).toThrow(/not seeded/);
    expect(interpolate(scope({ shimCommand: "node shim.js" }), "args[0]", "{{shimCommand}}")).toBe(
      "node shim.js",
    );
  });

  it("static validation: only declared vars and known steps may be referenced", () => {
    validatePlaceholders(capability, "args", "{{vars.tier}} {{steps.ask.answer}} {{objective}}");
    expect(() => validatePlaceholders(capability, "args", "{{vars.undeclared}}")).toThrow(
      /no declared default/,
    );
    expect(() => validatePlaceholders(capability, "args", "{{steps.nope.answer}}")).toThrow(
      /unknown step/,
    );
    expect(() => validatePlaceholders(capability, "args", "{{weird}}")).toThrow(
      /unknown placeholder/,
    );
  });
});
