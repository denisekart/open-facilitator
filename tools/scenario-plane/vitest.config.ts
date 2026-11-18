import { withCoverage } from "../../tools/coverage/vitest-coverage.mts";

export default withCoverage(
  {
    test: { include: ["test/**/*.test.ts"], testTimeout: 15_000 },
  },
  import.meta.dirname,
  "scenario-plane",
);
