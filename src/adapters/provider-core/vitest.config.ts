// Unit tier: config fail-closed law, ledger replay/conflict law, manifest
// flag filtering, webhook-receiver decode/outcome law — all in-process.
import { withCoverage } from "../../../tools/coverage/vitest-coverage.mts";

export default withCoverage(
  {
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["**/node_modules/**", "**/dist/**"],
    },
  },
  import.meta.dirname,
  "provider-core",
);
