// Unit tier: webhook HMAC-SHA256 law (`sha256=<hex>` form), normalize
// classification against docs-shaped fixtures, comment-ledger replay law,
// config validation — all in-process, no live GitHub.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**"],
  },
});
