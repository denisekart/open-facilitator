import js from "@eslint/js";
import tseslint from "typescript-eslint";

export default tseslint.config(
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    ignores: [
      "**/dist/",
      "node_modules/",
      "docs/",
      "src/protocol/src/generated/",
      "protocol/dotnet/",
      "**/*.svelte",
      "src/facilitator/wwwroot/",
      // .NET build output: the Playwright NuGet package stages a vendored
      // npm tree (CommonJS) under tests/Facilitator.E2E/bin — not ours to lint.
      "**/bin/",
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
      },
    },
  },
);
