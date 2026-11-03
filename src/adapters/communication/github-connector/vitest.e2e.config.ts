// E2E tier: the connector child + adapter + a scripted deterministic worker
// against a real Gateway + Facilitator (gateway-harness precedent) and a
// fake GitHub. Needs Postgres (FACILITATOR_PG) — run via `pnpm test:e2e`.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.e2e.test.ts"],
    testTimeout: 180_000,
    hookTimeout: 180_000,
  },
});
