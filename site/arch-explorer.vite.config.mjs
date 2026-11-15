// The explorer bundle is a plain <script>, so it must be a self-contained
// IIFE with its CSS emitted as a sibling file the page can <link>.
export default {
  plugins: [(await import("@sveltejs/vite-plugin-svelte")).svelte()],
  build: {
    outDir: ".explorer-dist",
    emptyOutDir: true,
    cssCodeSplit: false,
    minify: true,
    rollupOptions: {
      input: "src/arch-explorer/main.js",
      output: {
        format: "iife",
        inlineDynamicImports: true,
        entryFileNames: "arch-explorer.js",
        assetFileNames: (info) =>
          info.name && info.name.includes("css") ? "arch-explorer.css" : "[name][extname]",
      },
    },
  },
};
