// De-ansification law: colors/cursor/OSC gone, CR-redraws keep only the
// last write of a line, and a chunk ending mid-escape never leaks partial
// escape bytes into the matched window.
import { describe, expect, it } from "vitest";
import { DeansifyWindow, deansify } from "../src/playbook/ansi.js";

describe("deansify", () => {
  it("strips SGR colors and cursor movement", () => {
    expect(deansify("\x1b[31mProceed?\x1b[0m [y/N] \x1b[2K\x1b[1G")).toBe("Proceed? [y/N] ");
  });

  it("strips OSC window titles terminated by BEL or ST", () => {
    expect(deansify("\x1b]0;fake-tool\x07ready")).toBe("ready");
    expect(deansify("\x1b]0;fake-tool\x1b\\ready")).toBe("ready");
  });

  it("collapses CR-redraws to the last write of the line", () => {
    expect(deansify("working\rwriting\rdone\n")).toBe("done\n");
    expect(deansify("crlf-safe\r\nnext\r\n")).toBe("crlf-safe\nnext\n");
  });

  it("keeps newlines, tabs and printable text", () => {
    expect(deansify("a\tb\nc")).toBe("a\tb\nc");
  });
});

describe("DeansifyWindow", () => {
  it("reassembles an escape sequence split across chunks", () => {
    const window = new DeansifyWindow();
    window.push("yes\x1b[3");
    window.push("2mdone");
    expect(window.text()).toBe("yesdone");
  });

  it("holds a lone trailing ESC and completes it later", () => {
    const window = new DeansifyWindow();
    window.push("prompt\x1b");
    expect(window.text()).toBe("prompt");
    window.push("[0m! ");
    expect(window.text()).toBe("prompt! ");
  });

  it("caps the window to its byte budget (tail, not scrollback)", () => {
    const window = new DeansifyWindow(16);
    window.push("0123456789".repeat(5));
    expect(window.text()).toBe("4567890123456789");
  });
});
