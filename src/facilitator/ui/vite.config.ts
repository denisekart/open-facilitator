/// <reference types="vitest/config" />
import { svelte } from "@sveltejs/vite-plugin-svelte";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";
import { withCoverage } from "../../../tools/coverage/vitest-coverage.mjs";

// The SPA embeds into the kernel: hashed assets land in src/facilitator/wwwroot
// (served by UseStaticFiles + MapFallbackToFile). The dev proxy keeps the
// 5173-loop cookie/CORS-free: /api + /ui-config ride to the kernel on 8088.
export default defineConfig(({ mode }) =>
  withCoverage(
    {
      root: import.meta.dirname,
      base: "/",
      plugins: [svelte(), tailwindcss()],
      build: {
        outDir: "../wwwroot",
        emptyOutDir: true,
      },
      server: {
        proxy: {
          "/api": "http://localhost:8088",
          "/ui-config": "http://localhost:8088",
        },
      },
      // Component tests mount real components: resolve svelte's browser
      // (client) build, not the node default (the server build has no mount()).
      // Build MUST keep Vite's default conditions — an explicit array here
      // REPLACES them, and the SPA bundle silently ships the svelte server build.
      ...(mode === "test" ? { resolve: { conditions: ["browser"] } } : {}),
      test: {
        environment: "jsdom",
        setupFiles: ["./src/lib/test-harness.ts"],
        // jsdom+svelte cold start is ~34s PER FILE with the default fork
        // isolation, which (a) made the coverage leg the ladder's wall-clock
        // outlier and (b) trips vitest's hardcoded worker-start timeout under
        // suite load (observed: "Timeout waiting for worker to respond" on
        // FlowDetail.test.ts during `make coverage`). Workers are reused
        // across files; component tests keep state hygiene via the harness.
        poolOptions: { forks: { isolate: false } },
        server: {
          deps: {
            inline: ["svelte"],
          },
        },
      },
    },
    import.meta.dirname,
    "ui",
    // We ship the console: components count as production source.
    { include: ["src/**/*.ts", "src/**/*.svelte"] },
  ),
);
