// E2E tier: the connector child against a real Gateway + Facilitator
// (gateway-harness precedent). Needs Postgres (FACILITATOR_PG) — run via
// `pnpm test:e2e`.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.e2e.test.ts"],
    testTimeout: 120_000,
    hookTimeout: 120_000,
  },
});
