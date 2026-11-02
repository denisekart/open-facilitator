// E2E tier: each file spawns its OWN Facilitator process +
// database, so files run sequentially (six Kestrels + CREATE DATABASEs in
// parallel is flaky under resource contention) with generous timeouts —
// server boot is ~5 s and the replay test SIGSTOP/SIGKILLs a child process.
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
