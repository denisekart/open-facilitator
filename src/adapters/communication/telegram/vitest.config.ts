// Unit tier: normalize classification, config laws, media pipeline against a
// fake Telegram API + stub kernel, handler idempotency — all in-process, no
// live Telegram.
import { withCoverage } from "../../../../tools/coverage/vitest-coverage.mts";

export default withCoverage(
  {
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["**/node_modules/**", "**/dist/**"],
    },
  },
  import.meta.dirname,
  "telegram",
);
