// Unit tier: the lifecycle conformance kit (in-process Gateway listener)
// plus the coding harness driven in-process against a REAL local
// `git init --bare` remote (no compose, no kernel binary).
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**"],
  },
});
