// Unit tier: config law matrix + the scripted execution driven in-process
// with a fake RunContext (no sockets, no Facilitator).
import { withCoverage } from "../../../../tools/coverage/vitest-coverage.mjs";

export default withCoverage(
  {
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["**/node_modules/**", "**/dist/**", "**/*.e2e.test.ts"],
    },
  },
  import.meta.dirname,
  "codex",
);
