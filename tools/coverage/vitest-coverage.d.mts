// Types for vitest-coverage.mjs (see that file for the law). Kept as a
// declaration so importing package tsconfigs get types without pulling a
// cross-package source into their rootDir-scoped program.

export interface CoverageFragmentOptions {
  /** Source globs counted as shipped code; default the whole src tree. */
  include?: string[];
}

/** Raw `test.coverage` fragment for one package (see .mjs implementation). */
export declare function coverageConfig(
  dir: string,
  slug: string,
  opts?: CoverageFragmentOptions,
): { test: { coverage: Record<string, unknown> } };

/** mergeConfig(fragment, config): pass the config object, its own dir, and a unique slug. */
export declare function withCoverage<T extends object>(
  config: T,
  dir: string,
  slug: string,
  opts?: CoverageFragmentOptions,
): T;
