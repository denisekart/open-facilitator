// Shared coverage fragment for the measurement ladder (`make coverage`).
//
// Each package that owns a vitest/vite config merges this into its own config
// via `withCoverage({...}, import.meta.dirname, "<slug>")`. The fragment
// resolves the repo root by walking up to pnpm-workspace.yaml, so packages at
// different depths share one code path; the explicit slug keeps every
// reportsDirectory (`artifacts/coverage/ts/<slug>/lcov.info`) distinct even
// where sibling package dirs share a basename (github vs github-connector).
// `all: true` is deliberate: files no test imports count as 0%, so the
// baseline cannot flatter itself.
//
// Not part of any tsconfig program (vitest transpiles the importing config
// with esbuild); `pnpm lint` covers it as a tools/**/*.ts source file.
import fs from "node:fs";
import path from "node:path";
import { mergeConfig } from "vitest/config";

function repoRoot(start: string): string {
  let dir = start;
  for (;;) {
    if (fs.existsSync(path.join(dir, "pnpm-workspace.yaml"))) return dir;
    const parent = path.dirname(dir);
    if (parent === dir) throw new Error(`no pnpm-workspace.yaml above ${start}`);
    dir = parent;
  }
}

/**
 * Coverage block merged into a package's vitest/vite config.
 *
 * @param dir the config file's own directory (`import.meta.dirname`).
 * @param slug unique reportsDirectory/package name for this package.
 * @param opts.include what counts as shipped source; defaults to the whole
 *        `src/` tree — we ship `src`, so anything not imported by a test is
 *        a real 0%, not noise.
 */
export function coverageConfig(dir: string, slug: string, opts: { include?: string[] } = {}) {
  const include = opts.include ?? ["src/**/*.ts", "src/**/*.tsx"];
  return {
    test: {
      coverage: {
        provider: "v8" as const,
        reporter: ["lcov", "json-summary"] as const,
        // Absolute SF: lines so tools/coverage-report.mjs can map every file
        // to a repo-relative path regardless of package depth.
        absolutePath: true,
        reportsDirectory: path.join(repoRoot(dir), "artifacts", "coverage", "ts", slug),
        all: true,
        include,
        // Keep the whole shipped tree in scope: no default exclusions.
        exclude: [],
      },
    },
  };
}

/** mergeConfig wrapper so configs need a single fragment import. */
export function withCoverage<T>(
  config: T,
  dir: string,
  slug: string,
  opts: { include?: string[] } = {},
): T {
  return mergeConfig(config, coverageConfig(dir, slug, opts) as unknown as T);
}
