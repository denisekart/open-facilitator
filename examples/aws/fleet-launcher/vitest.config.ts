// Unit tier: the provision handler + rules gate + config parsing, all against
// a fake EcsApi seam and a fake link transport — no AWS, no kernel.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**"],
  },
});
