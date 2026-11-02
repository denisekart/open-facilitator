// Mattermost WebSocket v4 event listener (D1 — the inbound form).
// Mattermost 11.11 webhooks carry no signing secret, so trust comes from the
// bot credential instead: the adapter connects AS the bot and receives the
// same events its memberships entitle it to (channels it belongs to, its DMs).
//
// Law (live 11.11 wire shape): authenticate immediately on open — the
// server sends no unauthenticated greeting and idle-closes at ~5 s — then
// settle on the post-auth `hello` greeting, route `posted` /
// `reaction_added` / `direct_added` into the injected callback, and on every
// reconnect recover the events the socket missed by sequence number
// (GET /users/me/missed_events?seq=<last seen>).
import WebSocket from "ws";
import type { LinkLogger } from "@ofac/workspace-link";
import type { MattermostApi, WsEvent } from "./mm-client.js";

export interface WsListenerOptions {
  mmUrl: string;
  botToken: string;
  api: MattermostApi;
  onEvent(event: WsEvent): Promise<void>;
  onState(state: "up" | "down"): void;
  logger: LinkLogger;
  /** Test seam: WebSocket implementation + backoff ladder. */
  webSocketImpl?: typeof WebSocket;
  backoffMs?: readonly number[];
}

const DEFAULT_BACKOFF_MS = [250, 500, 1000, 2000, 5000, 10000];

function sleep(ms: number): Promise<void> {
  const { promise, resolve } = Promise.withResolvers<void>();
  setTimeout(resolve, ms);
  return promise;
}

const ROUTED: Record<string, true> = {
  posted: true,
  reaction_added: true,
  direct_added: true,
};

export class WsListener {
  private ws: WebSocket | null = null;
  private closed = false;
  private lastSeq = 0;
  private connected = false;
  private readonly backoff: readonly number[];
  private readonly wsImpl: typeof WebSocket;
  private readonly url: string;

  constructor(private readonly options: WsListenerOptions) {
    this.backoff = options.backoffMs ?? DEFAULT_BACKOFF_MS;
    this.wsImpl = options.webSocketImpl ?? WebSocket;
    const base = options.mmUrl.replace(/^http/i, "ws").replace(/\/+$/, "");
    this.url = `${base}/api/v4/websocket`;
  }

  get state(): "up" | "down" {
    return this.connected ? "up" : "down";
  }

  /** Connect; sends the challenge on open and resolves on the server's hello greeting. Reconnects until close(). */
  start(): Promise<void> {
    return this.connect(0);
  }

  async close(): Promise<void> {
    this.closed = true;
    this.ws?.terminate();
    this.ws = null;
  }

  private async connect(attempt: number): Promise<void> {
    if (this.closed) return;
    const open = Promise.withResolvers<void>();
    const greeted = { done: false };
    const ws = new this.wsImpl(this.url);
    this.ws = ws;
    ws.on("open", () => {
      // 11.11 sends NO unauthenticated greeting (idle-close at ~5 s): the
      // client authenticates immediately on open; the server then answers
      // with the `hello` event and {"status":"OK","seq_reply":1}.
      ws.send(
        JSON.stringify({
          seq: 1,
          action: "authentication_challenge",
          data: { token: this.options.botToken },
        }),
      );
    });
    ws.on("error", (error: Error) => {
      if (!greeted.done) open.reject(error);
    });
    ws.on("message", (raw: WebSocket.RawData) => {
      void this.handleFrame(raw, open, greeted);
    });
    ws.on("close", () => {
      this.connected = false;
      this.options.onState("down");
      if (!greeted.done) open.reject(new Error("ws closed before HELLO"));
      if (greeted.done && !this.closed) void this.revive(attempt + 1);
    });
    try {
      await open.promise;
      this.connected = true;
      this.options.onState("up");
    } catch (error) {
      ws.terminate();
      if (this.closed) throw new Error("mattermost ws unavailable at startup", { cause: error });
      this.options.logger.warn("mattermost ws connect failed, retrying", {
        error: String(error),
        attempt,
      });
      await sleep(this.backoff[Math.min(attempt, this.backoff.length - 1)]);
      await this.connect(attempt + 1);
    }
  }

  /** Post-reconnect path: recover the missed sequence window, then re-dial. */
  private async revive(attempt: number): Promise<void> {
    await sleep(this.backoff[Math.min(attempt - 1, this.backoff.length - 1)]);
    try {
      const missed = await this.options.api.missedEvents(this.lastSeq);
      for (const event of missed) {
        if (typeof event.seq === "number" && event.seq > this.lastSeq) this.lastSeq = event.seq;
        if (ROUTED[event.event ?? ""] === true) await this.deliver(event);
      }
    } catch (error) {
      this.options.logger.warn("ws missed-event recovery failed", { error: String(error) });
    }
    await this.connect(attempt);
  }

  private async handleFrame(
    raw: WebSocket.RawData,
    open: PromiseWithResolvers<void>,
    greeted: { done: boolean },
  ): Promise<void> {
    let event: WsEvent;
    try {
      event = JSON.parse(String(raw)) as WsEvent;
    } catch {
      return;
    }
    // The post-auth greeting: live 11.11 ships it lowercase (docs show
    // HELLO). It marks the connection authenticated.
    if ((event.event ?? "").toUpperCase() === "HELLO") {
      greeted.done = true;
      open.resolve();
      return;
    }
    if (typeof event.seq === "number" && event.seq > this.lastSeq) {
      this.lastSeq = event.seq;
    }
    if (event.event === "direct_added") {
      // Refresh the channel-type cache: a DM channel the bot just joined.
      const channelId = event.broadcast?.["channel_id"];
      if (typeof channelId === "string") {
        try {
          await this.options.api.channel(channelId);
        } catch (error) {
          this.options.logger.warn("direct_added channel fetch failed", {
            channelId,
            error: String(error),
          });
        }
      }
      return;
    }
    if (ROUTED[event.event ?? ""] === true) await this.deliver(event);
  }

  private async deliver(event: WsEvent): Promise<void> {
    try {
      await this.options.onEvent(event);
    } catch (error) {
      this.options.logger.error("inbound event handling failed", {
        event: event.event,
        error: String(error),
      });
    }
  }
}
