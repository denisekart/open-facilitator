// The adapter-side half of the loop budget (B6, N2), riding the SHARED
// provider-core counter: Telegram shows a bot-authored inbound message's
// author with `from.is_bot`, so a ping-pong with another bot is visible
// HERE before the kernel is involved at all. The law:
//   - own bot (echo): pass — the kernel's echo law owns bot-authored echoes;
//   - human: RESET the conversation's exchange count (a real reply restarts
//     the clock, the same law as the kernel half);
//   - foreign bot: CHARGE the counter; over the cap the update is dropped
//     (published nothing) and the stop is said ONCE per conversation via the
//     logger — the silence is observable, never a mystery.
// The kernel's `loop-budget` row stays the authority (it counts the
// facilitator's own replies); this guard only stops the adapter from feeding
// a runaway exchange back into the kernel in the first place.
import { createLoopBudget, type LoopBudget } from "@ofac/provider-core";
import type { LinkLogger } from "@ofac/workspace-link";
import type { TgUpdate } from "./normalize.js";

export interface InboundLoopGuard {
  /** Decide one inbound update BEFORE normalization. */
  decide(update: TgUpdate): "pass" | "drop";
}

export function createInboundLoopGuard(
  maxExchangesPerConversation: number,
  logger: LinkLogger,
): InboundLoopGuard {
  const disabled = maxExchangesPerConversation <= 0;
  const budget: LoopBudget = createLoopBudget({
    maxExchangesPerConversation: disabled ? Number.MAX_SAFE_INTEGER : maxExchangesPerConversation,
  });
  return {
    decide(update) {
      if (disabled) return "pass";
      const message = update.message ?? update.callback_query?.message;
      // A button press is a human act by definition (Telegram's own law:
      // callback queries carry a `from` user, never a bot).
      const author = update.message?.from ?? update.callback_query?.from;
      if (author === undefined || message?.chat === undefined) return "pass";
      const chatId = String(message.chat.id);
      if (author.is_bot !== true) {
        budget.reset(chatId);
        return "pass";
      }
      if (!budget.charge(chatId)) {
        const notice = budget.notice(chatId);
        if (notice !== undefined) logger.warn(notice, { chatId });
        return "drop";
      }

      return "pass";
    },
  };
}
