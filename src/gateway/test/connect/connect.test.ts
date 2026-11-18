// The child-side client's own edges, against a scripted raw server (no
// listener): the handshake deadline, the handler-crash reply, event publish,
// the goaway leg, and waitClosed.
import { createServer, type Socket } from "node:net";
import { afterEach, describe, expect, it } from "vitest";
import { builtinChildManifest } from "../../src/child.js";
import { connectChild } from "../../src/connect.js";
import { encodeFrame, type ChildWireFrame } from "../../src/childwire.js";

interface Scripted {
  port: number;
  socket: Promise<Socket>; // the first connection, for scripting
  close(): Promise<void>;
}

let live: Scripted | null = null;

afterEach(async () => {
  if (live !== null) await live.close();
  live = null;
});

/** A server that accepts one connection and hands it to the script. */
function scripted(): Promise<Scripted> {
  const socket = Promise.withResolvers<Socket>();
  const opened = Promise.withResolvers<number>();
  const server = createServer((s) => socket.resolve(s));
  const children = new Set<Socket>();
  server.on("connection", (s) => children.add(s));
  server.listen(0, "127.0.0.1", () => {
    const address = server.address();
    opened.resolve(typeof address === "object" && address !== null ? address.port : 0);
  });
  return opened.promise.then((port) => ({
    port,
    socket: socket.promise,
    close: () =>
      new Promise<void>((res) => {
        for (const s of children) s.destroy();
        server.close(() => res());
      }),
  }));
}

const event = {
  specversion: "1.0" as const,
  id: "evt-1",
  source: "urn:workspace:gw.t/kid",
  type: "org.facilitator.event.assignment.completed.v1",
  subject: "work/w/assignment/a",
  time: new Date().toISOString(),
  datacontenttype: "application/json" as const,
  dataschema: "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json",
  data: { executionId: "exec-1" },
};

function readFrames(socket: Socket, onFrame: (frame: ChildWireFrame) => void): void {
  let buffer = "";
  socket.on("data", (chunk: Buffer) => {
    buffer += chunk.toString("utf8");
    for (;;) {
      const nl = buffer.indexOf("\n");
      if (nl < 0) break;
      onFrame(JSON.parse(buffer.slice(0, nl)) as ChildWireFrame);
      buffer = buffer.slice(nl + 1);
    }
  });
}

describe("connectChild edges", () => {
  it("no register-ack inside the handshake window: connect rejects", async () => {
    const s = await scripted();
    live = s;
    const connecting = connectChild({
      childWorkspaceId: "gw.t/quiet",
      token: "t",
      manifest: builtinChildManifest("echo"),
      port: s.port,
      onCommand: async () => ({ kind: "accepted", executionId: "" }),
      handshakeTimeoutSeconds: 1,
    });
    await s.socket; // the server sees the hello but never answers
    await expect(connecting).rejects.toThrow(/register-ack timeout/);
  });

  it("a crashing child handler answers the gateway a rejected reply frame; events ride back", async () => {
    const s = await scripted();
    live = s;
    const conn = await (async () => {
      const connecting = connectChild({
        childWorkspaceId: "gw.t/crashy",
        token: "t",
        manifest: builtinChildManifest("echo"),
        port: s.port,
        onCommand: async () => {
          throw new Error("adapter panic");
        },
        heartbeatSeconds: 1,
      });
      const socket = await s.socket;
      socket.write(encodeFrame({ v: 1, type: "register-ack", accepted: true }));
      return { conn: await connecting, socket };
    })();
    const { socket } = conn;
    const reply = Promise.withResolvers<ChildWireFrame>();
    readFrames(socket, (frame) => {
      if (frame.type === "reply") reply.resolve(frame);
    });
    socket.write(
      encodeFrame({
        v: 1,
        type: "command",
        requestId: "ch-1",
        message: { kind: "status", executionId: "exec-9" },
      }),
    );
    expect(await reply.promise).toMatchObject({
      requestId: "ch-1",
      reply: { kind: "rejected", reason: "child handler failed" },
    });
    // Events ride back out as event frames (fire-and-forget wire).
    const frame = Promise.withResolvers<ChildWireFrame>();
    await conn.conn.publishEvent(event);
    socket.once("data", (chunk: Buffer) =>
      frame.resolve(JSON.parse(chunk.toString("utf8").split("\n")[0] ?? "") as ChildWireFrame),
    );
    expect(await frame.promise).toMatchObject({ type: "event", requestId: expect.stringMatching(/^ev-/) });
    // Post-ack socket death resolves waitClosed (the honest close, no throw).
    const closed = conn.conn.waitClosed();
    await conn.conn.close();
    await closed;
    expect(conn.conn.connected).toBe(false);
  });

  it("a goaway notice ends the connection honestly", async () => {
    const s = await scripted();
    live = s;
    const notices: string[] = [];
    const connecting = connectChild({
      childWorkspaceId: "gw.t/polite",
      token: "t",
      manifest: builtinChildManifest("echo"),
      port: s.port,
      onCommand: async () => ({ kind: "accepted", executionId: "" }),
      onNotice: (n) => notices.push(n.reason),
    });
    const socket = await s.socket;
    socket.write(encodeFrame({ v: 1, type: "register-ack", accepted: true }));
    socket.write(encodeFrame({ v: 1, type: "goaway", reason: "gateway-shutdown" }));
    socket.end();
    await (await connecting).waitClosed();
    expect(notices).toEqual(["gateway-shutdown"]);
  });
});
