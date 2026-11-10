// The long-poll loop (the mattermost ws-listener analogue): getUpdates with
// timeout=30 / limit=100 / allowed_updates message+edited_message+message_reaction,
// the update offset checkpointed in the chat-ledger kv (`update_offset`) so a
// restart never re-reads the whole queue window, and the backoff ladder
// [250,500,1000,2000,5000,10000] on transport failures (a long-poll HTTP
// timeout counts as an empty batch, not a failure).
import type { LinkLogger } from "@ofac/workspace-link";
import type { ChatLedger } from "./chat-ledger.js";
import type { TelegramApi } from "./tg-client.js";
import type { TgUpdate } from "./normalize.js";

export interface UpdatesListenerOptions {
  api: TelegramApi;
  state: ChatLedger;
  onUpdate(update: TgUpdate): Promise<void>;
  logger: LinkLogger;
  /** Test seam: backoff ladder (sleeps shrink to 0 when empty). */
  backoffMs?: readonly number[];
  /** Test seam: sleep implementation. */
  sleepImpl?: (ms: number) => Promise<void>;
  /** Long-poll hold seconds (plan law: 30). */
  timeoutSeconds?: number;
}

const DEFAULT_BACKOFF_MS = [250, 500, 1000, 2000, 5000, 10000];

const ALLOWED_UPDATES = ["message", "edited_message", "message_reaction"];

function sleep(ms: number): Promise<void> {
  const { promise, resolve } = Promise.withResolvers<void>();
  const timer = setTimeout(resolve, ms);
  timer.unref?.();
  return promise;
}

/** The checkpoint law: process, then store max(update_id)+1 — an update is
 *  only acknowledged once its handling (incl. publish) completed, so a crash
 *  mid-batch redelivers the whole batch and the kernel-side
 *  (provider, provider_delivery_id) dedupe absorbs it. */
export class UpdatesListener {
  private closed = false;
  private running: Promise<void> | null = null;
  private readonly backoff: readonly number[];
  private readonly nap: (ms: number) => Promise<void>;
  private connected = false;

  constructor(private readonly options: UpdatesListenerOptions) {
    this.backoff = options.backoffMs ?? DEFAULT_BACKOFF_MS;
    this.nap = options.sleepImpl ?? sleep;
  }

  get state(): "up" | "down" {
    return this.connected ? "up" : "down";
  }

  /** Resolve once the FIRST successful getUpdates round-trip completed (the
   *  readiness gate for health + tests). */
  private readonly firstOkGate = Promise.withResolvers<void>();
  started(): Promise<void> {
    return this.firstOkGate.promise;
  }

  /** Start the loop; returns immediately — the loop runs until close(). */
  start(): Promise<void> {
    if (this.running !== null) return this.running;
    this.running = this.loop();
    return this.running;
  }

  async close(): Promise<void> {
    this.closed = true;
    this.connected = false;
    this.firstOkGate.resolve();
  }

  /** One loop iteration, exposed so tests drive the loop deterministically. */
  async pollOnce(): Promise<number> {
    const offset = this.options.state.getKv("update_offset");
    const updates = await this.options.api.getUpdates({
      ...(offset === undefined ? {} : { offset: Number(offset) }),
      limit: 100,
      timeout: this.options.timeoutSeconds ?? 30,
      allowed_updates: [...ALLOWED_UPDATES],
    });
    let maxId = -1;
    for (const update of updates) {
      maxId = Math.max(maxId, update.update_id);
      await this.options.onUpdate(update);
    }
    if (maxId >= 0) this.options.state.setKv("update_offset", String(maxId + 1));
    return updates.length;
  }

  private async loop(): Promise<void> {
    let attempt = 0;
    while (!this.closed) {
      try {
        await this.pollOnce();
        this.connected = true;
        attempt = 0;
        this.firstOkGate.resolve();
      } catch (error) {
        this.connected = false;
        this.options.logger.warn("telegram getUpdates failed, backing off", {
          error: String(error),
          attempt,
        });
        await this.nap(this.backoff[Math.min(attempt, this.backoff.length - 1)] ?? 10000);
        attempt += 1;
      }
    }
    this.firstOkGate.resolve();
  }
}
