#!/usr/bin/env node
// `make scenario-plane-up`: park the fake provider plane on a fixed port so
// a developer can point an adapter's base URL at it and drive scenarios by
// hand (POST /__plane/*; GET /__plane/state). Ctrl-C stops it.
import { startFakeProvider } from "./fake-provider.ts";

const port = Number(process.env["SCENARIO_PLANE_PORT"] ?? "8791");
const plane = await startFakeProvider(port);
process.stdout.write(`scenario plane listening on ${plane.url} (control: /__plane/*)\n`);
process.on("SIGINT", () => {
  void plane.close().then(() => process.exit(0));
});
