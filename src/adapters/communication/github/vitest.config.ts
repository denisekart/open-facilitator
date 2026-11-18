// Unit tier: webhook HMAC-SHA256 law (`sha256=<hex>` form), normalize
// classification against docs-shaped fixtures, comment-ledger replay law,
// config validation — all in-process, no live GitHub.
import { withCoverage } from "../../../../tools/coverage/vitest-coverage.mts";

export default withCoverage(
  {
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["**/node_modules/**", "**/dist/**"],
    },
  },
  import.meta.dirname,
  "github",
);
