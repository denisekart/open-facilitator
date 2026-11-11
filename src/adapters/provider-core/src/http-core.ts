// The provider HTTP law (provider-agnostic): every connector call goes
// through `withRetry`, so 429/403 mean the SAME thing across vendors. A 429
// honors `Retry-After` (seconds or HTTP-date) up to a bounded wait; a 403 is
// a distinct, non-retryable class (a permission or quota wall — retrying it
// burns budget and changes nothing); other 5xx retry on the caller's backoff
// schedule. A stalled call never swallows the stall: the final HttpError
// carries status + retryAfterSeconds, which ops surfaces copy verbatim into
// `OpResult.degraded` (N7, F6, B6).
export class HttpError extends Error {
  constructor(
    readonly status: number,
    readonly detail?: string,
    readonly retryAfterSeconds?: number,
  ) {
    super(
      `HTTP ${status}${detail !== undefined && detail !== "" ? ` (${detail})` : ""}` +
        (retryAfterSeconds !== undefined ? ` retry-after=${retryAfterSeconds}s` : ""),
    );
    this.name = "HttpError";
  }

  /** The provider says FORBIDDEN — a wall, not a delay. */
  get forbidden(): boolean {
    return this.status === 403;
  }

  /** The provider says BACK OFF — a delay, honored, not swallowed. */
  get throttled(): boolean {
    return this.status === 429;
  }
}

/** `Retry-After` in both legal shapes (delta-seconds, HTTP-date); undefined
 *  when absent or unparseable (the caller then falls back to its backoff). */
export function parseRetryAfter(header: string | null | undefined, nowMs: number): number | undefined {
  if (header === undefined || header === null) return undefined;
  const trimmed = header.trim();
  if (trimmed === "") return undefined;
  const seconds = Number(trimmed);
  if (Number.isFinite(seconds)) {
    return seconds >= 0 && seconds <= 3600 ? Math.round(seconds) : undefined;
  }

  const when = Date.parse(trimmed);
  if (Number.isNaN(when)) return undefined;
  const delta = Math.round((when - nowMs) / 1000);
  return delta >= 0 && delta <= 3600 ? delta : undefined;
}

export interface RetryAttempt {
  /** Zero-based index of the attempt about to run. */
  attempt: number;
  /** Milliseconds to wait before this attempt (already decided). */
  delayMs: number;
  error: HttpError;
}

export interface WithRetryOptions {
  /** Delays between attempts; length caps the retry count. */
  backoffMs: readonly number[];
  /** Hard cap on a provider-imposed wait (a `Retry-After: 86400` is a bug report, not an instruction). */
  maxRetryAfterMs?: number;
  sleep?: (ms: number) => Promise<void>;
  /** Observe every stall (metrics/logs); never decides anything. */
  onRetry?: (info: RetryAttempt) => void;
  /** Which errors retry at all. Default: 429 + 5xx (403 never). */
  retryOn?: (error: HttpError) => boolean;
}

/** Run `fn`, applying the shared retry law. The LAST error propagates — an
 *  HttpError with its status + advisory wait intact, never a bare generic. */
export async function withRetry<T>(fn: () => Promise<T>, opts: WithRetryOptions): Promise<T> {
  const sleep =
    opts.sleep ??
    ((ms: number) => {
      const { promise, resolve } = Promise.withResolvers<void>();
      setTimeout(resolve, ms);
      return promise;
    });
  const retryOn = opts.retryOn ?? ((e: HttpError) => e.throttled || e.status >= 500);
  const maxRetryAfterMs = opts.maxRetryAfterMs ?? 60_000;
  let lastError: unknown;
  for (let attempt = 0; attempt <= opts.backoffMs.length; attempt++) {
    try {
      return await fn();
    } catch (error) {
      lastError = error;
      if (!(error instanceof HttpError) || !retryOn(error) || attempt === opts.backoffMs.length) {
        throw error;
      }

      // A provider-imposed wait is honored up to a hard cap; otherwise the
      // caller's own backoff schedule answers.
      const delayMs =
        error.retryAfterSeconds !== undefined
          ? Math.min(error.retryAfterSeconds * 1000, maxRetryAfterMs)
          : opts.backoffMs[attempt]!;
      opts.onRetry?.({ attempt: attempt + 1, delayMs, error });
      await sleep(delayMs);
    }
  }

  throw lastError;
}

/** Map a fetch Response onto the taxonomy: throws `HttpError` (with the
 *  honored `Retry-After`) on non-2xx, returns the parsed body otherwise. */
export async function httpErrorFrom(
  response: { status: number; headers: { get(name: string): string | null } },
  detail?: string,
  nowMs: number = Date.now(),
): Promise<HttpError> {
  return new HttpError(
    response.status,
    detail,
    parseRetryAfter(response.headers.get("retry-after"), nowMs),
  );
}
