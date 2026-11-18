import { withCoverage } from "../../tools/coverage/vitest-coverage.mts";

// Unit tier: conformance-fixture validation, no containers.
export default withCoverage(
  {
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["**/node_modules/**", "**/dist/**"],
    },
  },
  import.meta.dirname,
  "protocol",
);
