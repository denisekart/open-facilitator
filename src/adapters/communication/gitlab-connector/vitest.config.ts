// Unit tier: op law (ledger replay, ambiguous-timeout reconcile) against a
// fake GitLab HTTP server, the connector_actions ledger, config validation —
// all in-process, no Gateway, no live GitLab.
import { withCoverage } from "../../../../tools/coverage/vitest-coverage.mts";

export default withCoverage(
  {
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["**/node_modules/**", "**/dist/**", "**/*.e2e.test.ts"],
    },
  },
  import.meta.dirname,
  "gitlab-connector",
);
