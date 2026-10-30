// Unit tier: webhook HMAC law (both header forms), normalize classification
// against recorded payloads, comment-ledger replay law, config validation —
// all in-process, no live Forgejo.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**"],
  },
});
