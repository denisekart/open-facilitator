// Webhook registration on the connector face (I4): an integration is one
// command, not a wiki page. The vendor client implements the three primitives
// (create/list/delete against ITS API shape); the shared driver applies the
// idempotent law: ensure the hook for this URL exists — list first, create
// only when absent, and report what happened — so running the command twice
// (or against a hook a human made by hand) converges instead of duplicating.
export interface WebhookSpec {
  /** The URL the provider POSTs to — the kernel's ingress route for this provider. */
  url: string;
  /** Vendor event names ("pull_request", "Hook Events: push", …). */
  events: readonly string[];
  /** The HMAC secret the provider signs deliveries with (same key the
   *  receiver verifies). Providers that need more (GitLab's token header)
   *  take it from their client's own config, not this spec. */
  secret: string;
  insecureSsl?: boolean;
}

export interface WebhookRegistrationApi {
  list(): Promise<Array<{ id: string; url: string }>>;
  create(spec: WebhookSpec): Promise<string>;
  remove(id: string): Promise<void>;
}

export type WebhookEnsureOutcome =
  | { action: "already-registered"; id: string }
  | { action: "registered"; id: string };

/** Idempotent ensure: exact-URL match decides; a same-URL hook made by hand
 *  counts (the events list may differ - a human's choice is respected, the
 *  operator can re-register explicitly via the vendor UI/API). */
export async function ensureWebhook(
  api: WebhookRegistrationApi,
  spec: WebhookSpec,
): Promise<WebhookEnsureOutcome> {
  const hooks = await api.list();
  const existing = hooks.find((hook) => hook.url === spec.url);
  if (existing !== undefined) {
    return { action: "already-registered", id: existing.id };
  }

  return { action: "registered", id: await api.create(spec) };
}

/** Explicit replace: delete every hook for this URL, then create this one.
 *  The command behind "re-register the webhook" when the events or secret
 *  change. */
export async function replaceWebhook(
  api: WebhookRegistrationApi,
  spec: WebhookSpec,
): Promise<{ removed: string[]; id: string }> {
  const hooks = await api.list();
  const removed: string[] = [];
  for (const hook of hooks.filter((h) => h.url === spec.url)) {
    await api.remove(hook.id);
    removed.push(hook.id);
  }

  return { removed, id: await api.create(spec) };
}
