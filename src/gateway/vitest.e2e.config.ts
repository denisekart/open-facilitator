// E2E tier: each file spawns its OWN Facilitator process +
// database (workspace-link harness law), so files run sequentially with
// generous timeouts — server boot is ~5 s and the reconcile test SIGKILLs a
// gateway child process.
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
