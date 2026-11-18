// Unit tier: config law matrix + the fake-omp RPC loop driven in-process
// against the conformance kit (no Facilitator).
import { withCoverage } from "../../../../tools/coverage/vitest-coverage.mts";

export default withCoverage(
  {
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["**/node_modules/**", "**/dist/**", "**/*.e2e.test.ts"],
    },
  },
  import.meta.dirname,
  "omp",
);
