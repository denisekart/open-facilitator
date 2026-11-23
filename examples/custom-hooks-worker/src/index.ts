// examples/custom-hooks-worker — "you can also build a harness yourself
// with `HarnessHooks`". The trimmed deterministic worked example: when a
// run is a plain TypeScript script (no CLI to spawn, no playbook to
// interpret), implement HarnessHooks (src/harness.ts) and boot it with
// createAdapter from @ofac/adapter — this package is the shape, and its
// lifecycle test is how the kit proves it. The playbook-driven default is
// @ofac/harness-cli (see that package's README migration section).
export {
  createDeterministicHooks,
  type DeterministicHooks,
  type DeterministicRunOptions,
} from "./harness.js";
