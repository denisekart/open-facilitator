// The loop budget (B6, N2): echo suppression (isBotAuthored/mentionsBot)
// stops DIRECT loops; a budget stops the INDIRECT ones — two bots answering
// each other through human-visible comments never retrace the same two
// events, so every provider-side exchange counts here and stops at a config
// number. One counter per conversation: a bot-authored inbound event charges
// it; over the cap the normalizer DROPS the event and says so once per
// conversation (the silence itself must be observable, never a mystery).
export interface LoopBudget {
  /** Charge one bot-side exchange to a conversation; true when allowed to
   *  continue, false when the budget is spent (drop the event). */
  charge(conversationId: string): boolean;
  /** Whether the cap has been hit (the "say so" latch reads this). */
  exhausted(conversationId: string): boolean;
  /** The one-line notice to emit when the cap trips (idempotent per
   *  conversation: returns the text exactly once). */
  notice(conversationId: string): string | undefined;
  /** Human activity resets the exchange: a real reply restarts the clock. */
  reset(conversationId: string): void;
}

export function createLoopBudget(opts: {
  /** Bot-authored exchanges allowed per conversation before the stop. */
  maxExchangesPerConversation: number;
  noticeText?: string;
}): LoopBudget {
  const counts = new Map<string, number>();
  const announced = new Set<string>();
  const noticeText =
    opts.noticeText ??
    `Loop budget reached (${opts.maxExchangesPerConversation} bot exchanges in this conversation) - the facilitator stops speaking here until a human replies.`;

  return {
    charge(conversationId) {
      const used = (counts.get(conversationId) ?? 0) + 1;
      counts.set(conversationId, used);
      return used <= opts.maxExchangesPerConversation;
    },
    exhausted(conversationId) {
      return (counts.get(conversationId) ?? 0) > opts.maxExchangesPerConversation;
    },
    notice(conversationId) {
      if (!this.exhausted(conversationId) || announced.has(conversationId)) return undefined;
      announced.add(conversationId);
      return noticeText;
    },
    reset(conversationId) {
      counts.delete(conversationId);
      announced.delete(conversationId);
    },
  };
}

/** The classification half of the law: which inbound events are
 *  bot-authored (charge + maybe drop) versus human (reset). A provider
 *  normalizer wraps its echo filter with this so the budget and the filter
 *  stay ONE decision at every call site. */
export function withLoopBudget(
  budget: LoopBudget,
  inbound: { conversationId: string; actorProviderAccountId: string | undefined },
  botUser: string,
): { drop: boolean; notice: string | undefined } {
  if (inbound.actorProviderAccountId !== botUser) {
    budget.reset(inbound.conversationId);
    return { drop: false, notice: undefined };
  }

  const allowed = budget.charge(inbound.conversationId);
  return { drop: !allowed, notice: budget.notice(inbound.conversationId) };
}
