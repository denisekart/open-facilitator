// features/build.mjs — payload builder for the Dev Container Feature family.
//
// Builds every runtime package, then `pnpm deploy --prod` each Feature's
// runtime package into a self-contained tree and tars it to
// features/src/<name>/pkg/runtime.tar.gz with a sha256 sibling. The Feature
// install.sh layers consume the tarball: no npm registry, no network, no
// toolchain at container-install time.
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { copyFileSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const featuresDir = join(repoRoot, "features", "src");

/** Feature dir name → package to deploy + its bin entrypoint in the tree. */
const FEATURES = [
  { feature: "gateway", pkg: "@ofac/gateway", entry: "dist/bin/gateway.js" },
  { feature: "workspace-link", pkg: "@ofac/workspace-link", entry: "dist/bin/linkd.js" },
  { feature: "harness-adapter", pkg: "@ofac/harness-deterministic", entry: "dist/bin/worker.js" },
  { feature: "omp-adapter", pkg: "@ofac/harness-omp", entry: "dist/bin/worker.js" },
  { feature: "codex-adapter", pkg: "@ofac/harness-codex", entry: "dist/bin/worker.js" },
  { feature: "forgejo-provider", pkg: "@ofac/forgejo-connector", entry: "dist/bin/connector.js" },
];

const run = (cmd, args, opts = {}) =>
  execFileSync(cmd, args, { cwd: repoRoot, stdio: "inherit", ...opts });

// 1. Build every workspace package (idempotent; tsup is fast when fresh).
run("pnpm", ["-r", "build"]);

for (const { feature, pkg, entry } of FEATURES) {
  const pkgDir = join(featuresDir, feature);
  const staging = join(pkgDir, "pkg");
  mkdirSync(staging, { recursive: true });
  // Deploy outside the repo: pnpm deploy rewrites node_modules state, and
  // a staging tree inside the workspace root disturbs the shared links.
  const deployDir = join(tmpdir(), `ofac-feature-deploy-${feature}`);
  rmSync(deployDir, { recursive: true, force: true });
  mkdirSync(deployDir, { recursive: true });

  // 2. Self-contained production tree (workspace:* deps resolved to copies).
  run("pnpm", ["deploy", "--filter=" + pkg, "--prod", deployDir]);

  // 3. Tar the deployed tree (contents at archive root) + checksum sibling.
  const tarPath = join(staging, "runtime.tar.gz");
  run("tar", ["-czf", tarPath, "-C", deployDir, "."]);
  const digest = createHash("sha256")
    .update(readFileSync(tarPath))
    .digest("hex");
  writeFileSync(`${tarPath}.sha256`, `${digest}  runtime.tar.gz\n`);
  // The supervisor travels inside each feature's pkg dir: an OCI-published
  // feature image carries only its own subfolder, so install.sh cannot reach
  // a sibling shared/ directory.
  copyFileSync(join(repoRoot, "features", "shared", "ofac-supervisor.sh"), join(staging, "ofac-supervisor.sh"));
  rmSync(deployDir, { recursive: true, force: true });

  // 4. Assert the observable install contract: the bin entrypoint ships at
  // the deploy-tree root (pnpm deploy places the filtered package itself at
  // the root, deps — including every @ofac/* workspace dep — under
  // node_modules/), and native sqlite prebuilds ride along transitively.
  const listing = execFileSync("tar", ["-tzf", tarPath], { encoding: "utf8" });
  for (const needle of [`./${entry}`, "better-sqlite3/prebuilds/"]) {
    if (!listing.includes(needle)) {
      console.error(`FAIL: ${tarPath} lacks ${needle}`);
      process.exit(1);
    }
  }
  console.log(`ok: ${feature} → pkg/runtime.tar.gz (${digest.slice(0, 12)}…) contains ./${entry}`);
}
