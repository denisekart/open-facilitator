// Unit tier: system-hook token law, normalize classification against
// recorded fixtures, comment-ledger replay law, config validation — all
// in-process, no live GitLab.
import { withCoverage } from "../../../../tools/coverage/vitest-coverage.mts";

export default withCoverage(
  {
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["**/node_modules/**", "**/dist/**"],
    },
  },
  import.meta.dirname,
  "gitlab",
);
