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
    ],
  },
  {
    // Node/Bun tooling scripts (protocol/tools/*.mjs).
    files: ["**/*.mjs"],
    languageOptions: {
      ecmaVersion: 2024,
      sourceType: "module",
      globals: { console: "readonly", process: "readonly", URL: "readonly", Bun: "readonly" },
    },
  },
);
