// Unit tier: the provision handler + GC law + allowlist gate + config parsing,
// all against a fake Docker Engine API served over a unix socket in tmpdir —
// no real docker, no kernel.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**"],
  },
});
