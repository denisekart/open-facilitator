// Unit tier: config fail-closed law, ledger replay/conflict law, manifest
// flag filtering, webhook-receiver decode/outcome law — all in-process.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**"],
  },
});
