// ANSI/CR-redraw removal: the matching law. `promptMarkers` and `expect`
// run against the DE-ANSIFIED stream — a TUI repainting its status line
// must not smear a marker's bytes — and progress notes carry the same
// clean text. Raw stream bytes ship only as artifact bytes.
//
// Stripped: CSI sequences (colors, cursor, erase), OSC sequences (window
// titles), the two-byte C1 set, and CR-redraws (a bare CR means "rewrite
// this line": only the bytes after the last CR of the line survive).
// Retained: \n, \t and printable bytes.

// CSI: ESC [ params intermediates final. OSC: ESC ] ... terminated by BEL
// or ST (ESC \). Two-byte C1 (ESC + 0x40-0x5F) covers the rest.
// eslint-disable-next-line no-control-regex
const ANSI_RE = /\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(?:\x07|\x1b\\)|[@-Z\\-_])/g;
// One complete escape sequence anchored at the start of a string.
// eslint-disable-next-line no-control-regex
const ANSI_ANCHORED = /^\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(?:\x07|\x1b\\)|[@-Z\\-_])/;
const CRLF_RE = /\r\n/g;
// On the CRLF-normalized text, a bare CR is a redraw: delete everything
// from the line start through the LAST bare CR of that line (greedy).
const CR_REDRAW_RE = /[^\n]*\r/g;

/** Remove ANSI/CSI/OSC escapes and CR-redraws from a chunk of stream. */
export function deansify(text: string): string {
  // A bare CR means "rewrite this line": only the bytes after the last CR
  // of a line survive; CRLF stays a plain newline.
  return text.replace(ANSI_RE, "").replace(CRLF_RE, "\n").replace(CR_REDRAW_RE, "");
}

/**
 * Stream-safe de-ansifier: a chunk may end mid-escape, and a partial
 * escape must never leak into a matched window. Feed every chunk; read
 * the clean cumulative tail via `text()`.
 */
export class DeansifyWindow {
  private clean = "";
  private pending = "";
  private readonly maxBytes: number;

  constructor(maxBytes = 262_144) {
    this.maxBytes = maxBytes;
  }

  /** Ingest one raw chunk (any partial-escape tail is held, not dropped). */
  push(chunk: string): void {
    let merged = this.pending + chunk;
    this.pending = "";
    // Hold back a trailing ESC whose sequence is not (yet) complete, so
    // its bytes can never be matched or leak into the window.
    const esc = merged.lastIndexOf("\x1b");
    if (esc >= 0 && !ANSI_ANCHORED.test(merged.slice(esc))) {
      this.pending = merged.slice(esc);
      merged = merged.slice(0, esc);
    }
    this.clean = (this.clean + deansify(merged)).slice(-this.maxBytes);
  }

  /** The de-ansified cumulative text, capped to the last `maxBytes`. */
  text(): string {
    return this.clean;
  }
}
