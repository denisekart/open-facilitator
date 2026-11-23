// E2E tier: the REAL worker binary against a standalone
// Gateway + a dedicated Facilitator (workspace-link harness law); files run
// sequentially, boot is slow, and the crash law SIGKILLs the worker.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.e2e.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**"],
    fileParallelism: false,
    testTimeout: 120_000,
    hookTimeout: 180_000,
  },
});
