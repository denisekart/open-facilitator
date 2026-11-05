// Unit tier: config law matrix + the scripted execution driven in-process
// with a fake RunContext (no sockets, no Facilitator).
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**", "**/*.e2e.test.ts"],
  },
});
