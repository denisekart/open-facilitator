// The ask_user MCP leg, worker side: an in-process TCP bridge. The harness
// starts a bridge per execution, seeds $CODEX_HOME/config.toml with an
// [mcp_servers.ofac] entry launching the package shim, and answers bridge
// requests by calling `ctx.requestInput` on the LIVE run — so an ask_user
// tool call BLOCKS inside the adapter until the Facilitator routes a human
// answer back over the child wire (`respond`). When the run is aborted or
// the bridge closes, the request rejects honestly (no silent empty answer).
//
// Wire (newline-delimited JSON over loopback TCP):
//   -> {"id":1,"method":"ask_user","params":{"question":"..","choices":[..]?}}
//   <- {"id":1,"result":{"answer":".."}} | {"id":1,"error":".."}
import { createServer, type Server as NetServer, type Socket } from "node:net";
import { writeFileSync } from "node:fs";
import { join } from "node:path";
import type { LinkLogger } from "@ofac/workspace-link";

export interface AskUserParams {
  readonly question: string;
  readonly choices?: readonly string[] | undefined;
}

export interface AskUserBridge {
  /** `127.0.0.1:<port>` — the value of OFAC_MCP_BRIDGE for the shim. */
  readonly address: string;
  close(): Promise<void>;
}

export function startAskUserBridge(
  ask: (params: AskUserParams) => Promise<string>,
  logger: LinkLogger,
): Promise<AskUserBridge> {
  const { promise, resolve } = Promise.withResolvers<void>();
  const sockets = new Set<Socket>();
  const server: NetServer = createServer((socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
    socket.on("error", () => sockets.delete(socket));
    let buffer = "";
    socket.on("data", (chunk: Buffer) => {
      buffer += chunk.toString("utf8");
      for (;;) {
        const nl = buffer.indexOf("\n");
        if (nl < 0) break;
        const line = buffer.slice(0, nl).trim();
        buffer = buffer.slice(nl + 1);
        if (line.length > 0) void handle(socket, line);
      }
    });
  });

  async function handle(socket: Socket, line: string): Promise<void> {
    let frame: { id?: unknown; method?: unknown; params?: unknown };
    try {
      frame = JSON.parse(line) as typeof frame;
    } catch {
      return; // garbage on the bridge: dropped
    }
    if (frame.method !== "ask_user") {
      socket.write(`${JSON.stringify({ id: frame.id ?? null, error: "unknown method" })}\n`);
      return;
    }
    const params: unknown = frame.params;
    if (params === null || typeof params !== "object" || !("question" in params)) {
      socket.write(`${JSON.stringify({ id: frame.id ?? null, error: "question required" })}\n`);
      return;
    }
    const question: unknown = params.question;
    if (typeof question !== "string" || question.length === 0) {
      socket.write(`${JSON.stringify({ id: frame.id ?? null, error: "question required" })}\n`);
      return;
    }
    const rawChoices: unknown = "choices" in params ? params.choices : undefined;
    const kept = Array.isArray(rawChoices)
      ? rawChoices.filter((item): item is string => typeof item === "string")
      : undefined;
    try {
      const answer = await ask({ question, ...(kept !== undefined ? { choices: kept } : {}) });
      if (socket.destroyed) return;
      socket.write(`${JSON.stringify({ id: frame.id ?? null, result: { answer } })}\n`);
    } catch (error) {
      logger.warn("ask-user-bridge-rejected", {
        message: error instanceof Error ? error.message : String(error),
      });
      if (!socket.destroyed) {
        socket.write(
          `${JSON.stringify({ id: frame.id ?? null, error: "run no longer waiting for input" })}\n`,
        );
      }
    }
  }

  server.listen(0, "127.0.0.1", () => resolve());
  return promise.then(() => {
    // Node's untagged socket-address union; we only ever listen on TCP.
    const addr = server.address() as { port: number };
    const port = addr.port;
    return {
      address: `127.0.0.1:${String(port)}`,
      close: async () => {
        for (const socket of sockets) socket.destroy();
        sockets.clear();
        await new Promise<void>((res) => server.close(() => res()));
      },
    };
  });
}

/**
 * Seed `$CODEX_HOME/config.toml` for THIS run: the ask_user MCP entry
 * launching the package shim, which connects back to the bridge address.
 * Codex reads the file at exec startup, so the harness rewrites it before
 * every spawn (serialized by the spawn chain — the next run may touch the
 * file only after the previous codex has booted).
 */
export function writeCodexConfig(
  codexHome: string,
  shimEntry: string,
  bridgeAddress: string,
): string {
  const toml = `# managed by ofac-codex-worker — rewritten before every codex exec
[mcp_servers.ofac]
command = "node"
args = ["${shimEntry}"]
env = { OFAC_MCP_BRIDGE = "${bridgeAddress}" }
startup_timeout_sec = 20
`;
  const path = join(codexHome, "config.toml");
  writeFileSync(path, toml);
  return path;
}
