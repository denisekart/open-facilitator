// One human vocabulary for machine states, shared by every view. The kernel
// wire format stays untouched; this only decides how states READ in the UI:
// plain words, one semantic color each, and time shown as "how long ago"
// with the exact UTC stamp one hover away.

export type Tone = "neutral" | "progress" | "wait" | "ok" | "bad" | "motion";

const TONES: Record<string, Tone> = {
  // works
  open: "neutral",
  routed: "progress",
  inprogress: "progress",
  in_progress: "progress",
  awaitingapproval: "wait",
  awaiting_approval: "wait",
  done: "ok",
  cancelled: "neutral",
  // assignments / attempts / deliveries
  pending: "wait",
  assigned: "progress",
  accepted: "progress",
  started: "progress",
  completed: "ok",
  failed: "bad",
  dead_letter: "bad",
  delivered: "ok",
  suppressed: "neutral",
  // approvals
  approved: "ok",
  denied: "bad",
  // links / gateways
  online: "ok",
  offline: "neutral",
  // artifacts / manifests
  available: "neutral",
  validated: "ok",
  rejected: "bad",
  // outbox/inbox
  published: "ok",
  // enrollment tokens
  active: "ok",
  revoked: "neutral",
  expired: "neutral",
  // memories
  indexed: "ok",
  // executions
  running: "motion",
  succeeded: "ok",
};

// Plain-word overrides where the wire name would read as jargon.
const LABELS: Record<string, string> = {
  dead_letter: "dead letter",
  inprogress: "in progress",
  in_progress: "in progress",
  awaitingapproval: "awaiting approval",
  awaiting_approval: "awaiting approval",
};

export function stateLabel(state: string): string {
  return LABELS[state] ?? state.replace(/_/g, " ");
}

export function stateTone(state: string): Tone {
  return TONES[state] ?? "neutral";
}

/** Tailwind classes for a state badge, keyed only off the tone. */
export const TONE_CLASS: Record<Tone, string> = {
  neutral: "bg-sunken text-ink-2 ring-edge",
  progress: "bg-accent-veil text-accent ring-accent/25",
  wait: "bg-amber-veil text-amber ring-amber/25",
  ok: "bg-ok-veil text-ok ring-ok/25",
  bad: "bg-danger-veil text-danger ring-danger/25",
  motion: "bg-amber-veil text-amber ring-amber/25",
};

function fmtUTC(d: Date): string {
  return d
    .toISOString()
    .replace("T", " ")
    .replace(/\.\d+Z$/, "Z");
}

/**
 * "3 min ago" for a machine stamp; the full UTC instant rides in `title`.
 * Returns { text, title } — always render the title so the precise time is
 * one hover away.
 */
export function ago(
  iso: string | null,
): { text: string; title: string } | { text: string; title: null } {
  if (!iso) return { text: "never", title: null };
  const when = new Date(iso);
  if (Number.isNaN(when.getTime())) return { text: iso, title: null };
  const s = Math.max(0, (Date.now() - when.getTime()) / 1000);
  const unit = (n: number, u: string) => `${n} ${u}${n === 1 ? "" : "s"}`;
  const text =
    s < 45
      ? "just now"
      : s < 90
        ? "about a minute ago"
        : s < 3600
          ? `${unit(Math.round(s / 60), "min")} ago`
          : s < 86400
            ? `${unit(Math.round(s / 3600), "hour")} ago`
            : s < 86400 * 30
              ? `${unit(Math.round(s / 86400), "day")} ago`
              : when.toISOString().slice(0, 10);
  return { text, title: fmtUTC(when) };
}

/** Exact UTC stamp, human-spaced — for tooltips and detail rows. */
export function stamp(iso: string | null): string {
  if (!iso) return "—";
  const when = new Date(iso);
  return Number.isNaN(when.getTime()) ? iso : fmtUTC(when);
}
