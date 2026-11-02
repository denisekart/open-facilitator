// E2E standalone: a STANDALONE gateway boots with an empty
// fleet — the design's zero-child allowance — links + reconciles + is ready,
// the kernel serves no manifest, and a child that shows up LATER is
// accepted and union-POSTs. The late child then reconnects the fleet to
// life: diagnostics report it connected with generation 1.
import { describe, expect, it } from "vitest";
import {
  E2E,
  childReady,
  gatewayConfig,
  gatewayStatus,
  gatewayToken,
  startChild,
  startGateway,
  useFacilitator,
  waitFor,
  type Harness,
} from "./gateway-harness.js";
import { linkGet } from "../../../workspace-link/test/e2e/harness.js";

describe.skipIf(!E2E)("gateway standalone e2e", () => {
  const harness: Harness = useFacilitator();

  it("boots childless, accepts a late child, union-POSTs its manifest", async () => {
    const gw = await gatewayConfig({
      facilitatorUrl: harness.baseUrl,
      fleet: [],
      gatewayMode: "standalone",
      heartbeatSeconds: 2,
    });
    const gateway = startGateway(gw.configPath);
    let child = null;
    try {
      const ready = await gateway.ready();
      expect(ready.children).toBe(0);
      const status = await gatewayStatus(gw.diagnosticsPort);
      expect(status.mode).toBe("standalone");
      expect(status.reconciled).toBe(true);
      expect(status.children).toEqual([]);

      // No manifest exists yet: the kernel answers 404.
      const before = await linkGet(
        harness.baseUrl,
        "/api/link/v1/capabilities",
        gatewayToken(gw.stateDir),
      );
      expect(before.status).toBe(404);

      // A late child is accepted even though config named no fleet.
      child = startChild({
        childWorkspaceId: `${gw.gatewayId}/late`,
        socket: gw.socketPath ?? "",
        token: "tok",
        mode: "silent",
        capability: "research.analyze",
      });
      await childReady(child);
      const online = await waitFor("late child online", async () => {
        const s = await gatewayStatus(gw.diagnosticsPort);
        return s.children?.length === 1 && s.children[0]?.state === "connected" ? s : false;
      });
      expect(online.children?.[0]?.childGeneration).toBe(1);

      // Its manifest union-POSTs: the kernel now serves it, team-owned.
      const caps = await waitFor("union POST lands", async () => {
        const r = await linkGet(
          harness.baseUrl,
          "/api/link/v1/capabilities",
          gatewayToken(gw.stateDir),
        );
        return r.status === 200 ? (r.body as { ownerTeam: string }) : false;
      });
      expect(caps.ownerTeam).toBe("team-alpha");
    } finally {
      child?.kill("SIGKILL");
      await gateway.kill("SIGTERM");
    }
  });
});
