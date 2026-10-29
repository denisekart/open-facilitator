// The D1 inbound leg's handshake law, pinned against the LIVE 11.11 wire
// shape: the server sends NO unauthenticated greeting (idle-close ~5 s), so
// the listener authenticates immediately on open with an
// authentication_challenge carrying the bot token, and the connection is
// settled by the post-auth `hello` greeting (lowercase on the live server;
// docs show HELLO). A close before the greeting retries, never kills.
import { describe, expect, it } from "vitest";
import EventEmitter from "node:events";
import { WsListener } from "../src/ws-listener.js";
import type { MattermostApi } from "../src/mm-client.js";
import type { LinkLogger } from "@ofac/workspace-link";

class FakeSocket extends EventEmitter {
  sent: string[] = [];
  terminate() {
    this.emit("close");
  }
  send(data: string) {
    this.sent.push(data);
  }
}

const quiet: LinkLogger = {
  info: () => {},
  warn: () => {},
  error: () => {},
  debug: () => {},
};

const fakeApi = {
  missedEvents: async () => [],
  channel: async () => ({ type: "O" }),
} as unknown as MattermostApi;

function listenerWith(behavior: (socket: FakeSocket, attempt: number) => void) {
  let attempts = 0;
  class FakeWs extends FakeSocket {
    constructor() {
      super();
      attempts += 1;
      const a = attempts;
      queueMicrotask(() => {
        this.emit("open");
        behavior(this, a);
      });
    }
  }
  const listener = new WsListener({
    mmUrl: "http://mm.invalid",
    botToken: "tok",
    api: fakeApi,
    onEvent: async () => {},
    onState: () => {},
    logger: quiet,
    webSocketImpl: FakeWs as never,
    backoffMs: [1, 1],
  });
  return { listener, attempts: () => attempts };
}

describe("ws handshake", () => {
  it("authenticates on open and settles on the lowercase hello greeting", async () => {
    let socket: FakeSocket | undefined;
    const { listener } = listenerWith((s) => {
      socket = s;
      // The challenge must ride immediately on open (no server greeting).
      expect(JSON.parse(s.sent[0] ?? "{}").action).toBe("authentication_challenge");
      s.emit("message", Buffer.from(JSON.stringify({ event: "hello", seq: 1 })));
    });
    await listener.start();
    expect(socket).toBeDefined();
    const challenge = JSON.parse(socket!.sent[0] ?? "{}");
    expect(challenge.data.token).toBe("tok");
    expect(listener.state).toBe("up");
    await listener.close();
  });

  it("retries when the server closes before the greeting", async () => {
    let second: FakeSocket | undefined;
    const { listener, attempts } = listenerWith((s, a) => {
      if (a === 1) s.emit("close");
      else {
        second = s;
        s.emit("message", Buffer.from('{"event":"hello","seq":2}'));
      }
    });
    await listener.start();
    expect(attempts()).toBe(2);
    expect(second!.sent.length).toBe(1);
    await listener.close();
  });
});
