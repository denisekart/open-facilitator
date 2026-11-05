// childwire unit tier: pure codec tests — chunk splitting, batching, size
// gate, malformed-line isolation, direction gate, round-trip of every frame.
import { describe, expect, it } from "vitest";
import {
  MAX_CHILD_FRAME_BYTES,
  createFrameParser,
  encodeFrame,
  type ChildWireFrame,
  type FrameParseError,
} from "../../src/childwire.js";
import type { CapabilityManifestV1, EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";

const manifest: CapabilityManifestV1 = {
  apiVersion: "facilitator.framework/v1",
  kind: "CapabilityManifest",
  ownerTeam: "child-echo",
  capabilities: [
    {
      id: "research.analyze",
      version: 1,
      execution: "asynchronous",
      acceptedContexts: ["uri-reference"],
      outputKinds: ["analysis.report"],
      outputMediaTypes: ["text/markdown"],
      supportsSteering: true,
      supportsCancellation: true,
      supportsNudge: false,
    },
  ],
};

const envelope: EnvelopeCommand = {
  id: "env-1",
  type: "org.facilitator.command.work.assign.v1",
  source: "urn:facilitator",
  time: "2026-10-24T00:00:00Z",
  specversion: "1.0",
  subject: "work/w1/assignment/a1",
  data: {
    commandId: "cmd-1",
    workId: "w1",
    assignmentId: "a1",
    capabilityId: "research.analyze",
    context: [],
    attempt: 1,
  },
} as unknown as EnvelopeCommand;

const event: EnvelopeEvent = {
  id: "ev-1",
  type: "org.facilitator.event.assignment.completed.v1",
  source: "urn:workspace:gw.t/echo",
  time: "2026-10-24T00:00:00Z",
  specversion: "1.0",
  subject: "work/w1/assignment/a1",
  data: { workId: "w1", assignmentId: "a1", executionId: "exec-1" },
} as unknown as EnvelopeEvent;

const hello: ChildWireFrame = {
  v: 1,
  type: "hello",
  childWorkspaceId: "gw.t/echo",
  token: "sekret",
  manifest,
};

function collector(expectDir: "child" | "gateway") {
  const frames: ChildWireFrame[] = [];
  const errors: FrameParseError[] = [];
  const parser = createFrameParser(
    (f) => frames.push(f),
    (e) => errors.push(e),
    { expect: expectDir },
  );
  return { frames, errors, parser };
}

describe("encodeFrame", () => {
  it("emits one compact line with a single trailing newline", () => {
    const text = encodeFrame(hello);
    expect(text.endsWith("\n")).toBe(true);
    expect(text.trimEnd().split("\n")).toHaveLength(1);
    expect(JSON.parse(text)).toEqual(hello);
  });
});

describe("createFrameParser", () => {
  it("reassembles a frame split across chunks", () => {
    const { frames, parser } = collector("child");
    const text = encodeFrame(hello);
    parser.push(text.slice(0, 10));
    parser.push(text.slice(10, 40));
    expect(frames).toHaveLength(0);
    parser.push(text.slice(40));
    expect(frames).toEqual([hello]);
    expect(parser.pendingBytes()).toBe(0);
  });

  it("splits two frames delivered in one chunk (buffer concat)", () => {
    const { frames, parser } = collector("child");
    const heartbeat: ChildWireFrame = { v: 1, type: "heartbeat", childWorkspaceId: "gw.t/echo" };
    parser.push(Buffer.from(encodeFrame(hello) + encodeFrame(heartbeat)));
    expect(frames).toEqual([hello, heartbeat]);
  });

  it("round-trips every frame type in both directions", () => {
    const childFrames: ChildWireFrame[] = [
      hello,
      {
        v: 1,
        type: "reply",
        requestId: "ch-1",
        reply: { kind: "accepted", executionId: "exec-1" },
      },
      { v: 1, type: "event", requestId: "ch-2", envelope: event },
      { v: 1, type: "heartbeat", childWorkspaceId: "gw.t/echo" },
    ];
    const gatewayFrames: ChildWireFrame[] = [
      {
        v: 1,
        type: "command",
        requestId: "ch-3",
        message: {
          kind: "start",
          executionId: "exec-1",
          executionGeneration: 4,
          command: envelope,
        },
      },
      {
        v: 1,
        type: "command",
        requestId: "ch-4",
        message: { kind: "status", executionId: "exec-1" },
      },
      { v: 1, type: "register-ack", accepted: true },
      { v: 1, type: "register-ack", accepted: false, reason: "duplicate-id" },
      { v: 1, type: "goaway", reason: "gateway-shutdown" },
    ];
    const up = collector("child");
    for (const frame of childFrames) up.parser.push(encodeFrame(frame));
    expect(up.frames).toEqual(childFrames);
    const down = collector("gateway");
    for (const frame of gatewayFrames) down.parser.push(encodeFrame(frame));
    expect(down.frames).toEqual(gatewayFrames);
  });

  it("respond command frame: round-trips and enforces its full identity", () => {
    const respond: ChildWireFrame = {
      v: 1,
      type: "command",
      requestId: "ch-9",
      message: { kind: "respond", executionId: "exec-1", requestId: "inp-1", text: "postgres" },
    };
    const down = collector("gateway");
    down.parser.push(encodeFrame(respond));
    expect(down.frames).toEqual([respond]);
    expect(down.errors).toHaveLength(0);

    // Gateway→child respond MUST carry executionId + requestId + text
    // (adapter.v1.json respondMessage law); a stripped frame is dropped
    // alone like any malformed line.
    for (const broken of [
      '{"v":1,"type":"command","requestId":"ch-a","message":{"kind":"respond","executionId":"exec-1","text":"x"}}\n',
      '{"v":1,"type":"command","requestId":"ch-b","message":{"kind":"respond","executionId":"exec-1","requestId":"inp-1"}}\n',
      '{"v":1,"type":"command","requestId":"ch-c","message":{"kind":"respond","executionId":"exec-1","requestId":"","text":"x"}}\n',
    ]) {
      down.parser.push(broken);
    }
    expect(down.errors).toHaveLength(3);
    expect(down.errors.every((e) => e.kind === "parse-error")).toBe(true);
    expect(down.frames).toEqual([respond]);

    // Direction gate stays law: a respond frame rides gateway→child only;
    // a child-side socket drops it like any wrong-direction frame.
    const up = collector("child");
    up.parser.push(encodeFrame(respond));
    expect(up.errors).toHaveLength(1);
    expect(up.frames).toHaveLength(0);
  });

  it("oversized line: error once, connection stops emitting", () => {
    const { frames, errors, parser } = collector("child");
    // The gate fires the moment buffered-but-unterminated bytes exceed it —
    // no newline needed to prove the line is doomed.
    parser.push(Buffer.alloc(MAX_CHILD_FRAME_BYTES + 8, 0x61));
    expect(errors).toEqual([{ kind: "oversize", bytes: MAX_CHILD_FRAME_BYTES + 8 }]);
    parser.push(Buffer.from("bb\n"));
    parser.push(encodeFrame(hello)); // dead after oversize
    expect(frames).toHaveLength(0);
    expect(errors).toHaveLength(1);
  });

  it("oversized complete line is caught before parse", () => {
    const { errors, parser } = collector("child");
    const big = `${"x".repeat(MAX_CHILD_FRAME_BYTES + 1)}\n`;
    parser.push(big);
    expect(errors).toHaveLength(1);
    expect(errors[0]?.kind).toBe("oversize");
  });

  it("malformed JSON line is dropped alone; the connection survives", () => {
    const { frames, errors, parser } = collector("child");
    parser.push(`{not json\n${encodeFrame(hello)}`);
    expect(errors).toHaveLength(1);
    expect(errors[0]?.kind).toBe("parse-error");
    expect(frames).toEqual([hello]);
  });

  it("rejects a wrong-version or wrong-direction frame like a malformed line", () => {
    const up = collector("child");
    up.parser.push('{"v":2,"type":"hello"}\n');
    up.parser.push('{"v":1,"type":"command","requestId":"r","message":{}}\n'); // gateway→child on a child socket
    up.parser.push("[1,2]\n");
    up.parser.push(encodeFrame(hello));
    expect(up.errors).toHaveLength(3);
    expect(up.errors.every((e) => e.kind === "parse-error")).toBe(true);
    expect(up.frames).toEqual([hello]);
  });

  it("ignores blank lines between frames", () => {
    const { frames, errors, parser } = collector("child");
    parser.push(`\n${encodeFrame(hello)}\n`);
    expect(frames).toEqual([hello]);
    expect(errors).toHaveLength(0);
  });
});
