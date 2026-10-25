import { defineConfig } from "vitest/config";

// Unit tier: an in-process child listener + scripted Gateway-side frames
// (the @ofac/gateway listener IS the peer — no Facilitator needed). The
// lifecycle contract kit (`./testing`) runs inside a consumer's vitest.
export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**", "**/*.e2e.test.ts"],
  },
});
