/// <reference types="vitest/config" />
import { svelte } from "@sveltejs/vite-plugin-svelte";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";

// The SPA embeds into the kernel: hashed assets land in src/facilitator/wwwroot
// (served by UseStaticFiles + MapFallbackToFile). The dev proxy keeps the
// 5173-loop cookie/CORS-free: /api + /ui-config ride to the kernel on 8088.
export default defineConfig(({ mode }) => ({
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
  resolve: {
    conditions: mode === "test" ? ["browser"] : [],
  },
  test: {
    environment: "jsdom",
    server: {
      deps: {
        inline: ["svelte"],
      },
    },
  },
}));
