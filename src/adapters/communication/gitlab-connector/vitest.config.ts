// Unit tier: op law (ledger replay, ambiguous-timeout reconcile) against a
// fake GitLab HTTP server, the connector_actions ledger, config validation —
// all in-process, no Gateway, no live GitLab.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**", "**/*.e2e.test.ts"],
  },
});
