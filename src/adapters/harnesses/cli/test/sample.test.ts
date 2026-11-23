// The sample research playbook, validated in-repo: it must parse and the
// manifest it derives must match the byte template the deterministic
// worker advertised (examples/slim capabilities.yaml), modulo the accepted
// parity delta (supportsSteering true→false on research.analyze). This is
// the parity gate that does not need a Gateway.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { parse } from "yaml";
import { loadPlaybookFile } from "../src/playbook/load.js";
import { interpolate } from "../src/playbook/interpolate.js";

const SAMPLE = join(import.meta.dirname, "..", "sample", "research.playbook.yaml");

describe("sample research playbook", () => {
  it("parses with its helper scripts beside it", () => {
    const playbook = loadPlaybookFile(SAMPLE);
    expect([...playbook.capabilities.keys()]).toEqual(["research.analyze", "research.publish"]);
    expect(playbook.playbookDir).toBe(join(import.meta.dirname, "..", "sample"));
    expect(playbook.binaries.has("node")).toBe(true);
    // The tool path each step names, once its placeholder resolves, exists.
    const first = playbook.capabilities.get("research.analyze")?.steps[0]?.run?.args[0];
    expect(first).toBe("{{playbookDir}}/fake-research-tool.mjs");
    const resolved = interpolate(
      {
        workId: "w",
        assignmentId: "a",
        objective: "o",
        attachments: "",
        playbookDir: playbook.playbookDir,
        vars: {},
        steps: new Map(),
      },
      "args[0]",
      String(first),
    );
    expect(resolved).toBe(join(playbook.playbookDir, "fake-research-tool.mjs"));
    readFileSync(resolved); // throws if the sample drifted
  });

  it("derives the deterministic manifest pair (steering delta accepted)", () => {
    const playbook = loadPlaybookFile(SAMPLE);
    const template = parse(
      readFileSync(
        join(
          import.meta.dirname,
          "..",
          "..",
          "deterministic",
          "sample",
          "capabilities.yaml",
        ),
        "utf8",
      ),
    ) as { capabilities: Record<string, unknown>[] };
    expect(playbook.manifest.ownerTeam).toBe("team-alpha");
    expect(playbook.manifest.capabilities).toHaveLength(2);
    for (const expected of template.capabilities) {
      const derived = playbook.manifest.capabilities.find((c) => c.id === expected["id"]);
      expect(derived, `capability ${String(expected["id"])}`).toBeDefined();
      for (const [key, value] of Object.entries(expected)) {
        if (key === "supportsSteering") continue; // the accepted parity delta
        expect(derived?.[key as keyof typeof derived]).toEqual(value);
      }
      expect(derived?.supportsSteering).toBe(false);
    }
  });

  it("the optional human leg rides the config-injected askQuestion var", () => {
    const playbook = loadPlaybookFile(SAMPLE);
    const ask = playbook.capabilities.get("research.analyze")?.steps.find((s) => s.name === "ask");
    expect(ask?.when).toBe("askQuestion");
    expect(ask?.ask?.question).toBe("{{vars.askQuestion}}");
    // No declared default: without config askQuestion the step never runs,
    // matching the deterministic worker's optional askQuestion exactly.
    expect(playbook.capabilities.get("research.analyze")?.vars).toBeUndefined();
  });
});
