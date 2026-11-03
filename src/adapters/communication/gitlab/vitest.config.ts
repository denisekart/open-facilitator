// Unit tier: system-hook token law, normalize classification against
// recorded fixtures, comment-ledger replay law, config validation — all
// in-process, no live GitLab.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**"],
  },
});
