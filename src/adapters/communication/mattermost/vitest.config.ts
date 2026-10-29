// Unit tier: normalize classification, ledger replay law, channel-cache
// behavior, config validation — all in-process, no live Mattermost.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**"],
  },
});
