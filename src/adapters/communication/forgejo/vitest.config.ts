// Unit tier: webhook HMAC law (both header forms), normalize classification
// against recorded payloads, comment-ledger replay law, config validation —
// all in-process, no live Forgejo.
import { withCoverage } from "../../../../tools/coverage/vitest-coverage.mjs";

export default withCoverage(
  {
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["**/node_modules/**", "**/dist/**"],
    },
  },
  import.meta.dirname,
  "forgejo",
);
