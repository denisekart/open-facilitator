// Localhost diagnostics: one route, GET /status, no auth — dev-tier honesty
// mirroring the kernel's /api/dev. Bind failure is fatal at startup (exit 1):
// a Gateway that cannot report truth is broken.
import { createServer } from "node:http";
import type { GatewayRuntime, GatewayStatus } from "./runtime.js";

export interface DiagnosticsHandle {
  port: number;
  close(): Promise<void>;
}

export async function startDiagnostics(
  runtime: GatewayRuntime,
  port: number,
): Promise<DiagnosticsHandle | null> {
  if (port === 0) return null;
  const server = createServer((req, res) => {
    if (req.method === "GET" && req.url === "/status") {
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify(runtime.gatewayStatus()));
      return;
    }
    res.writeHead(404, { "content-type": "application/json" });
    res.end(JSON.stringify({ error: "not-found" }));
  });
  server.on("clientError", (_error, socket) => {
    socket.end("HTTP/1.1 400 Bad Request\r\n\r\n");
  });
  const bound = Promise.withResolvers<void>();
  server.once("error", (error) => bound.reject(error));
  server.once("listening", () => bound.resolve());
  server.listen(port, "127.0.0.1");
  try {
    await bound.promise;
  } catch (error) {
    throw new Error(
      `diagnostics cannot bind 127.0.0.1:${port}: ${error instanceof Error ? error.message : String(error)}`,
      { cause: error },
    );
  }
  return {
    port,
    close: async () => {
      const closed = Promise.withResolvers<void>();
      server.closeAllConnections();
      server.close((error) => (error ? closed.reject(error) : closed.resolve()));
      await closed.promise;
    },
  };
}

export type { GatewayStatus };
