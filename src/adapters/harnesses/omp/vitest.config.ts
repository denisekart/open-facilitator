// Unit tier: config law matrix + the fake-omp RPC loop driven in-process
// against the conformance kit (no Facilitator).
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**", "**/*.e2e.test.ts"],
  },
});
