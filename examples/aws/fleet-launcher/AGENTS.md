# examples/aws/fleet-launcher/

## Purpose
Non-production AWS reference launcher: a link-plane client (team account,
outbound-only) that dials the kernel and runs an ECS fleet from a local
static rules file. Unsupported, not part of the supported adapter set —
the supported provisioning launcher is `src/adapters/provisioning-oci`.

## Invariants
- The client contract MIRRORS the oci launcher (enroll body
  `{token, endpointId, instanceId, trustMode:"isolated-executor", role:"child"}`,
  capabilities publish, CommandOutcome acked/delivered/rejected law,
  random-per-start instanceId unless `OFAC_INSTANCE_ID`). When oci's law
  changes, mirror it here or document the divergence.
- Rules file IS the allowlist: unknown `workloadId` → `rejected` receipt;
  spec `image`/`network` are ignored by design (the kernel never pins
  placement — `fleet-rules.json` does, the docker-socket analog).
- `$NAME` env name-indirection law: a sole-`$NAME` spec value resolves from
  the launcher's own env; raw `$NAME` is what crossed the wire.
- Stop law: in-memory `workloadId → taskArn` for THIS generation; stop of
  anything untracked is an accepted no-op (oci's "nothing to stop" law).
- Env surface (task-def contract): `OFAC_KERNEL_URL`, `OFAC_ENROLL_TOKEN`,
  `OFAC_ENDPOINT_ID`, `OFAC_TEAM`, `OFAC_RULES_FILE`, optional
  `OFAC_INSTANCE_ID`/`OFAC_STATE_PATH`; fail-closed naming each missing var.

## Truth anchors
- `src/handler.ts` — receipt law + `planRun` + env expansion;
  `src/ecs.ts` — the `EcsApi` seam (mock in `test/fake-ecs.ts`).
- Mirrored-from: `../../src/adapters/provisioning-oci/src/{index,handler}.ts`.
- Stack that deploys this: `../lib/fleet.ts` (`TeamFleetStack`, opt-in via
  team env entry `"fleet": true`).
