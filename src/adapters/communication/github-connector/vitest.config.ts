import { withCoverage } from "../../../../tools/coverage/vitest-coverage.mjs";

// Unit tier: config/ops tests over the connector, no containers. The E2E
// tier (`*.e2e.test.ts`) needs a live Gateway + Postgres and runs via
// `pnpm test:e2e` (vitest.e2e.config.ts).
export default withCoverage(
  {
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["**/node_modules/**", "**/dist/**", "**/*.e2e.test.ts"],
    },
  },
  import.meta.dirname,
  "github-connector",
);
