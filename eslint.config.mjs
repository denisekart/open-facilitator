import js from "@eslint/js";
import tseslint from "typescript-eslint";

export default tseslint.config(
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    ignores: [
      "**/dist/",
      "node_modules/",
      "site/node_modules/",
      "site/vendor/",
      "site/dist/",
      "docs/",
      "src/protocol/src/generated/",
      "protocol/dotnet/",
      "**/*.svelte",
      "**/cdk.out/",
      "src/facilitator/wwwroot/",
      // .NET build output: the Playwright NuGet package stages a vendored
      // npm tree (CommonJS) under tests/Facilitator.E2E/bin — not ours to
      // lint. 2026-11-02: narrowed from `**/bin/` so the TypeScript
      // packages' src/bin entrypoints stay linted like every other source.
      "**/bin/Debug/",
      "**/bin/Release/",
      "**/obj/",
    ],
  },
  {
    // Node/Bun tooling scripts (protocol/tools/*.mjs).
    files: ["**/*.mjs"],
    languageOptions: {
      ecmaVersion: 2024,
      sourceType: "module",
      globals: {
        console: "readonly",
        process: "readonly",
        URL: "readonly",
        Bun: "readonly",
        fetch: "readonly",
        setTimeout: "readonly",
        clearTimeout: "readonly",
      },
    },
  },
  {
    // Browser runtime for the static site (site/src/js).
    files: ["site/src/js/**/*.js"],
    languageOptions: {
      ecmaVersion: 2024,
      sourceType: "script",
      globals: {
        console: "readonly",
        document: "readonly",
        window: "readonly",
        location: "readonly",
        history: "readonly",
        navigator: "readonly",
        fetch: "readonly",
        FormData: "readonly",
        URLSearchParams: "readonly",
        CSS: "readonly",
        SVGTitleElement: "readonly",
        innerWidth: "readonly",
        innerHeight: "readonly",
        setTimeout: "readonly",
        clearTimeout: "readonly",
        requestAnimationFrame: "readonly",
      },
    },
  },
);
