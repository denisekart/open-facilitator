# Custom-hooks worker — build a harness yourself

You can also build a harness yourself with `HarnessHooks`: when the run is
plain TypeScript (no CLI to spawn, no playbook to interpret), implement the
two hooks — `onExecute(ctx)` (run the work, report progress, return an
outcome) and `onSteer(directive, ctx)` — and boot them with
`createAdapter` from `@ofac/adapter`. That is the whole surface, and this
package is the shape.

`src/harness.ts` is the trimmed deterministic worked example: a two-step,
zero-randomness research run that completes over the kernel artifact path
(report + canonical Result Manifest uploaded, `resultManifestRef` +
`resultDigest` on the completed event, no self-declared digests). An abort
unwinds as `cancelled` with NO terminal event; a failed upload is an honest
failure (throw ⇒ the SDK publishes `assignment.failed`).

`test/lifecycle.test.ts` runs the SDK's lifecycle contract kit
(`runAdapterLifecycleContract` from `@ofac/adapter/testing`) against these
hooks — the same kit the shipped harnesses conform against — plus a
no-steer variant proving the kit asserts the `unsupported` reply.

The playbook-driven default (spawn any CLI, declare steps in YAML) is
[`@ofac/harness-cli`](../../src/adapters/harnesses/cli/README.md); its
migration section maps the old deterministic-worker config keys onto
playbook equivalents.
