// Unit tier: normalize classification, ledger replay law, channel-cache
// behavior, config validation — all in-process, no live Mattermost.
import { withCoverage } from "../../../../tools/coverage/vitest-coverage.mjs";

export default withCoverage(
  {
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["**/node_modules/**", "**/dist/**"],
    },
  },
  import.meta.dirname,
  "mattermost",
);
