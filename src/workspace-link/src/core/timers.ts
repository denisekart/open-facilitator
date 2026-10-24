// Timer utilities shared by every scheduling site in the SDK. Node returns a
// `Timeout` with `unref()` from setTimeout/setInterval (browsers return a
// number); one helper keeps the SDK silent on the event loop without scattering
// casts across the call sites.
export const DEFAULT_BACKOFF = [1_000, 2_000, 5_000, 10_000, 30_000] as const;

/** Keep-alive semantics: never hold the Node event loop open for this timer. */
export function unrefTimer(timer: { unref?: () => void }): void {
  timer.unref?.();
}

/** Unref'd sleep: resolves after `ms` without pinning the event loop. */
export function sleep(ms: number): Promise<void> {
  const { promise, resolve } = Promise.withResolvers<void>();
  unrefTimer(setTimeout(resolve, ms));
  return promise;
}
