import { defineConfig } from "vitest/config";

// Unit tier: mock-transport tests over the runtime, no containers. The E2E
// tier (`*.e2e.test.ts`) needs a live Facilitator (FACILITATOR_URL) and runs
// only via `pnpm test:e2e` / the CI gateway-e2e job.
export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**", "**/*.e2e.test.ts"],
  },
});
