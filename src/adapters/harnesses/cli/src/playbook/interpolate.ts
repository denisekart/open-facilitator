// The interpolation law: playbook text may reference run state ONLY through
// declared slots — `{{vars.*}}`, `{{workId}}`, `{{assignmentId}}`,
// `{{objective}}`, `{{attachments}}`, `{{shimCommand}}`, and the per-step
// `{{steps.<name>.answer}}` / `{{steps.<name>.exitCode}}`. Substitution
// lands ONLY in declared `args`/`env` values and a few display fields; a
// payload can never name a binary or add a flag (the allow-listed binaries
// + declared `allowedEnvKeys` are the whole payload surface).
import { PlaybookError, type PlaybookCapability } from "./schema.js";

/** Values a run can interpolate. Missing lookup ⇒ error, never empty
 * string: a silent blank turns a typo into a wrong command line. */
export interface InterpolationScope {
  readonly workId: string;
  readonly assignmentId: string;
  readonly objective: string;
  /** Rendered attachment lines (already de-risked by the harness). */
  readonly attachments: string;
  /** Absolute dir of the playbook file: steps name their helper scripts
   * relative to their own runbook, never to the run cwd. */
  readonly playbookDir: string;
  /** `node <mcp-shim.js>` + bridge env note, when the ask leg is seeded. */
  readonly shimCommand?: string | undefined;
  /** inputs.vars over the capability's declared defaults. */
  readonly vars: Readonly<Record<string, string>>;
  /** Per completed step: answer/exitCode, keyed by step name. */
  readonly steps: ReadonlyMap<string, StepRecord>;
}

export interface StepRecord {
  readonly answer?: string | undefined;
  readonly exitCode?: number | undefined;
}

const PLACEHOLDER_RE = /\{\{([a-zA-Z0-9_.]+)\}\}/g;

function lookup(scope: InterpolationScope, path: string, where: string): string {
  if (path === "workId") return scope.workId;
  if (path === "assignmentId") return scope.assignmentId;
  if (path === "objective") return scope.objective;
  if (path === "attachments") return scope.attachments;
  if (path === "playbookDir") return scope.playbookDir;
  if (path === "shimCommand") {
    if (scope.shimCommand === undefined) {
      throw new PlaybookError(where, "{{shimCommand}} used but the ask leg is not seeded");
    }
    return scope.shimCommand;
  }
  if (path.startsWith("vars.")) {
    const name = path.slice("vars.".length);
    const value = scope.vars[name];
    if (value === undefined) {
      throw new PlaybookError(
        where,
        `{{vars.${name}}} is not declared (capability 'vars' or the assignment's inputs.vars)`,
      );
    }
    return value;
  }
  const stepMatch = /^steps\.([a-zA-Z0-9_-]+)\.(answer|exitCode)$/.exec(path);
  if (stepMatch !== null) {
    const [, stepName, field] = stepMatch as unknown as [string, string, string];
    const record = scope.steps.get(stepName);
    if (record === undefined) {
      throw new PlaybookError(
        where,
        `{{${path}}} references unknown or unfinished step '${stepName}'`,
      );
    }
    const value = field === "answer" ? record.answer : String(record.exitCode ?? "");
    if (value === undefined) {
      throw new PlaybookError(where, `{{${path}}} but step '${stepName}' recorded no ${field}`);
    }
    return value;
  }
  throw new PlaybookError(where, `unknown placeholder '{{${path}}}'`);
}

/** Substitute every placeholder in one declared slot value. */
export function interpolate(scope: InterpolationScope, where: string, text: string): string {
  return text.replace(PLACEHOLDER_RE, (_whole, path: string) => lookup(scope, path, where));
}

/** Validate before ANY substitution: the playbook may only reference
 * placeholders whose source exists (declared vars, known step names).
 * Unknown-by-shape placeholders always fail. */
export function validatePlaceholders(
  capability: PlaybookCapability,
  where: string,
  text: string,
): void {
  for (const match of text.matchAll(PLACEHOLDER_RE)) {
    const path = String(match[1]);
    if (
      path === "workId" ||
      path === "assignmentId" ||
      path === "objective" ||
      path === "attachments" ||
      path === "playbookDir" ||
      path === "shimCommand"
    ) {
      continue;
    }
    if (path.startsWith("vars.")) {
      const name = path.slice("vars.".length);
      if (capability.vars?.[name] === undefined) {
        throw new PlaybookError(
          where,
          `{{vars.${name}}} has no declared default on capability '${capability.id}'`,
        );
      }
      continue;
    }
    const stepMatch = /^steps\.([a-zA-Z0-9_-]+)\.(answer|exitCode)$/.exec(path);
    if (stepMatch === null) {
      throw new PlaybookError(where, `unknown placeholder '{{${path}}}'`);
    }
    const stepName = String(stepMatch[1]);
    if (!capability.steps.some((step) => step.name === stepName)) {
      throw new PlaybookError(where, `{{${path}}} references unknown step '${stepName}'`);
    }
  }
}
