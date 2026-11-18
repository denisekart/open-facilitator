import { withCoverage } from "../../tools/coverage/vitest-coverage.mts";

// Unit tier: mock-transport tests + fixture conformance, no containers.
// E2E tier (`*.e2e.test.ts`) needs a live Facilitator (FACILITATOR_URL) and
// runs only via `pnpm test:e2e` / the CI link-e2e job.
export default withCoverage(
  {
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
  },
  import.meta.dirname,
  "workspace-link",
);
