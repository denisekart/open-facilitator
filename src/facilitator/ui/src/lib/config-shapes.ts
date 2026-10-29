/**
 * The closed config registry mirrored from AdminConfigStore.cs validators —
 * drives the PATCH forms (int ranges, enum selects, string bounds).
 * Stored-intent semantics: writes are audited intent, not live toggles.
 */
export type FieldSpec =
  | { kind: "int"; name: string; min: number; max: number }
  | { kind: "enum"; name: string; values: string[] }
  | { kind: "string"; name: string; min: number; max: number };

export interface ResourceSpec {
  resource: string;
  fields: FieldSpec[];
}

export const CONFIG_RESOURCES: ResourceSpec[] = [
  {
    resource: "notifications",
    fields: [
      { kind: "enum", name: "replyVsReaction", values: ["reply", "reaction"] },
      { kind: "enum", name: "progressVerbosity", values: ["minimal", "normal", "verbose"] },
    ],
  },
  {
    resource: "retry-policy",
    fields: [
      { kind: "int", name: "maxAttempts", min: 1, max: 20 },
      { kind: "int", name: "backoffCapSeconds", min: 1, max: 3600 },
    ],
  },
  {
    resource: "nudge-policy",
    fields: [{ kind: "int", name: "staleAfterMinutes", min: 1, max: 1440 }],
  },
  {
    resource: "workload-scale",
    fields: [
      { kind: "int", name: "minWorkers", min: 0, max: 8 },
      { kind: "int", name: "maxWorkers", min: 1, max: 32 },
      { kind: "int", name: "idleShutdownMinutes", min: 1, max: 1440 },
    ],
  },
  {
    resource: "routing-defaults",
    fields: [{ kind: "string", name: "provider", min: 1, max: 128 }],
  },
];

/** Client-side mirror of the workload-scale min<=max rule. */
export function localValidationError(
  spec: ResourceSpec,
  payload: Record<string, unknown>,
): string | null {
  if (spec.resource === "workload-scale") {
    const min = payload["minWorkers"];
    const max = payload["maxWorkers"];
    if (typeof min === "number" && typeof max === "number" && min > max) {
      return "'minWorkers' must not exceed 'maxWorkers'";
    }
  }
  return null;
}

/**
 * The config conflict state machine (step 5): a 409 carries the current row;
 * the UI reloads and explains who changed what and why.
 */
export interface ConflictState {
  kind: "conflict";
  author: string;
  reason: string;
  rowVersion: number;
}

export function describeConflict(conflict: ConflictState): string {
  return `changed by ${conflict.author} (${conflict.reason}) — re-apply your edit`;
}
