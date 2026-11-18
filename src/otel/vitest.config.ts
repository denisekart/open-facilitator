import { withCoverage } from "../../tools/coverage/vitest-coverage.mjs";

// Unit tier: env-only bootstrap tests, no network (port 1 black-holes).
export default withCoverage(
  {
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["**/node_modules/**", "**/dist/**"],
      // The emitting branch cold-imports the whole SDK tree per module graph;
      // that exceeds the 5s default under coverage.
      testTimeout: 60_000,
    },
  },
  import.meta.dirname,
  "otel",
);
