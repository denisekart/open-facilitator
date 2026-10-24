import { defineConfig } from "vitest/config";

// Unit tier: mock-transport tests + fixture conformance, no containers.
// E2E tier (`*.e2e.test.ts`) needs a live Facilitator (FACILITATOR_URL) and
// runs only via `pnpm test:e2e` / the CI link-e2e job.
export default defineConfig({
  test: {
    include: [
      "test/client/**/*.test.ts",
      "test/ledger/**/*.test.ts",
      "test/transport/**/*.test.ts",
      "test/faults/**/*.test.ts",
      "test/package/**/*.test.ts",
    ],
    exclude: ["**/node_modules/**", "**/dist/**", "**/*.e2e.test.ts"],
  },
});
