import { defineConfig } from "vitest/config";

// Unit tier: env-only bootstrap tests, no network (port 1 black-holes).
export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**"],
  },
});
