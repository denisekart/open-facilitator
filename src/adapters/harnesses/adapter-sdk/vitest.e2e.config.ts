// E2E tier: the deterministic harness's lifecycle suite + worker.e2e live in
// the deterministic package (it owns the binary); this package's `./testing`
// export is consumed there. The config stays for the shared CI contract
// (`test:e2e` script parity across workspace packages).
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
