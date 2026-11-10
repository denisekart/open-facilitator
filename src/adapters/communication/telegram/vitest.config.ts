// Unit tier: normalize classification, config laws, media pipeline against a
// fake Telegram API + stub kernel, handler idempotency — all in-process, no
// live Telegram.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**"],
  },
});
