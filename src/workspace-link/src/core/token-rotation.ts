// On an unauthorized transport failure, run the token refresh hook once.
// Shared by every send path (connect, hello, receipt, event publish, outbox
// drain): the 401 answer is always "rotate, then let the caller's retry
// ladder re-issue", never an inline resend.
import { TransportError } from "./errors.js";
import type { LinkLogger, LinkTokenProvider } from "./types.js";

export class TokenRotation {
  constructor(
    private readonly deps: {
      token: LinkTokenProvider;
      logger: LinkLogger;
      endpointId: string;
    },
  ) {}

  async maybeRefresh(error: unknown): Promise<void> {
    if (error instanceof TransportError && error.unauthorized && this.deps.token.refresh) {
      try {
        await this.deps.token.refresh();
        this.deps.logger.info("link token refreshed after 401", {
          endpointId: this.deps.endpointId,
        });
      } catch (refreshError) {
        this.deps.logger.warn("token refresh failed", { reason: String(refreshError) });
      }
    }
  }
}
