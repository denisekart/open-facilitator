// The ask_user MCP leg, worker side: an in-process TCP bridge. The harness
// starts a bridge per execution, writes the omp-auto-loaded `.mcp.json`
// (Claude-compatible, probe law: filename `.mcp.json` in the session cwd)
// pointing at the node shim, and answers bridge requests by calling
// `ctx.requestInput` — so an ask_user tool call BLOCKS inside the adapter
// until the Facilitator routes a human answer back over the child wire.
//
// Wire (newline-delimited JSON over TCP, loopback only):
//   -> {"id":1,"method":"ask_user","params":{"question":"..","choices":[..]?}}
//   <- {"id":1,"result":{"answer":".."}} | {"id":1,"error":".."}
import { createServer, type Server as NetServer, type Socket } from "node:net";
import { writeFileSync } from "node:fs";
import { join } from "node:path";

export interface AskUserParams {
  readonly question: string;
  readonly choices?: readonly string[] | undefined;
}

export interface AskUserBridge {
  /** `127.0.0.1:<port>` — the value of OFAC_MCP_BRIDGE for the shim. */
  readonly address: string;
  close(): Promise<void>;
}

export async function startAskUserBridge(
  ask: (params: AskUserParams) => Promise<string>,
): Promise<AskUserBridge> {
  const { promise, resolve } = Promise.withResolvers<void>();
  const sockets = new Set<Socket>();
  const server: NetServer = createServer((socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
    let buf = "";
    socket.on("data", (chunk: Buffer) => {
      buf += chunk.toString("utf8");
      for (;;) {
        const nl = buf.indexOf("\n");
        if (nl < 0) break;
        const line = buf.slice(0, nl).trim();
        buf = buf.slice(nl + 1);
        if (line === "") continue;
        void handle(socket, line);
      }
    });
  });
  const handle = async (socket: Socket, line: string): Promise<void> => {
    let frame: Record<string, unknown>;
    try {
      frame = JSON.parse(line) as Record<string, unknown>;
    } catch {
      return;
    }
    const id = frame["id"];
    const reply = (value: Record<string, unknown>): void => {
      if (!socket.writable) return;
      socket.write(`${JSON.stringify({ id, ...value })}\n`);
    };
    if (frame["method"] !== "ask_user") {
      reply({ error: `unknown method '${String(frame["method"] ?? "")}'` });
      return;
    }
    const params = (frame["params"] ?? {}) as Record<string, unknown>;
    const question = typeof params["question"] === "string" ? params["question"] : "";
    if (question === "") {
      reply({ error: "ask_user requires a non-empty question" });
      return;
    }
    const choices = Array.isArray(params["choices"])
      ? params["choices"].filter((c): c is string => typeof c === "string")
      : undefined;
    try {
      const answer = await ask({
        question,
        ...(choices !== undefined && choices.length > 0 ? { choices } : {}),
      });
      reply({ result: { answer } });
    } catch (error) {
      reply({ error: error instanceof Error ? error.message : String(error) });
    }
  };
  server.listen(0, "127.0.0.1", () => resolve());
  await promise;
  const port = (server.address() as { port: number }).port;
  return {
    address: `127.0.0.1:${String(port)}`,
    close: async () => {
      for (const socket of sockets) socket.destroy();
      await new Promise<void>((r) => server.close(() => r()));
    },
  };
}

/**
 * The MCP config omp auto-loads from the session cwd (probe law): a
 * Claude-compatible `.mcp.json` launching the package shim, which connects
 * back to the bridge address.
 */
export function writeMcpConfig(repoDir: string, shimEntry: string, bridgeAddress: string): string {
  const path = join(repoDir, ".mcp.json");
  writeFileSync(
    path,
    `${JSON.stringify(
      {
        mcpServers: {
          ofac: {
            command: process.execPath,
            args: [shimEntry],
            env: { OFAC_MCP_BRIDGE: bridgeAddress },
          },
        },
      },
      null,
      2,
    )}\n`,
  );
  return path;
}
