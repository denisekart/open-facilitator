import { withCoverage } from "../../tools/coverage/vitest-coverage.mts";

// Unit tier: env-only bootstrap tests, no network (port 1 black-holes).
export default withCoverage(
  {
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["**/node_modules/**", "**/dist/**"],
    },
  },
  import.meta.dirname,
  "otel",
);
