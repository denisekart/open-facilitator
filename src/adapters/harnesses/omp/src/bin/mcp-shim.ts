// ofac-omp MCP shim: a stdio MCP server (spawned by the harness via the
// generated .mcp.json) exposing the single ask_user tool. The tool call
// forwards to the worker-process bridge (OFAC_MCP_BRIDGE=127.0.0.1:<port>)
// and BLOCKS until the human's answer rides back through the Facilitator;
// the tool result is {answer}. When the bridge answers with an error (the
// run was aborted, the adapter closed), the tool call fails honestly.
import { createConnection } from "node:net";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";

const TOOL = {
  name: "ask_user",
  description:
    "Ask the human one question through the Open Facilitator and wait for the answer. Use when blocked on a decision only a human can make.",
  inputSchema: {
    type: "object",
    properties: {
      question: { type: "string", description: "The question, in plain text." },
      choices: {
        type: "array",
        items: { type: "string" },
        description: "Optional shortlist of acceptable answers (<=8).",
      },
    },
    required: ["question"],
    additionalProperties: false,
  },
} as const;

function textArg(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function stringArray(value: unknown): string[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const kept = value.filter((item): item is string => typeof item === "string");
  return kept.length === 0 ? undefined : kept;
}

/** One request per connection: connect, write one frame, await its reply. */
function askBridge(address: string, params: Record<string, unknown>): Promise<string> {
  const [host, portText] = address.split(":");
  const port = Number(portText);
  if (host === undefined || !Number.isInteger(port) || port <= 0 || port > 65_535) {
    return Promise.reject(new Error(`bad OFAC_MCP_BRIDGE '${address}'`));
  }
  const { promise, resolve, reject } = Promise.withResolvers<string>();
  const socket = createConnection({ host, port });
  let buf = "";
  let settled = false;
  const finish = (fn: () => void): void => {
    if (settled) return;
    settled = true;
    socket.destroy();
    fn();
  };
  socket.on("error", (error) => finish(() => reject(error)));
  socket.on("close", () => finish(() => reject(new Error("ask_user bridge closed unexpectedly"))));
  socket.on("data", (chunk: Buffer) => {
    buf += chunk.toString("utf8");
    const nl = buf.indexOf("\n");
    if (nl < 0) return;
    let frame: unknown;
    try {
      frame = JSON.parse(buf.slice(0, nl));
    } catch {
      return;
    }
    if (typeof frame !== "object" || frame === null) return;
    const record = frame as Record<string, unknown>; // parsed JSON line: object checked, fields narrowed below
    const error = textArg(record["error"]);
    if (error !== undefined) {
      finish(() => reject(new Error(error)));
      return;
    }
    const result = record["result"];
    const answer =
      typeof result === "object" && result !== null
        ? textArg((result as Record<string, unknown>)["answer"])
        : undefined;
    if (answer !== undefined) finish(() => resolve(answer));
    else finish(() => reject(new Error("ask_user bridge replied without an answer")));
  });
  socket.on("connect", () => {
    socket.write(`${JSON.stringify({ id: 1, method: "ask_user", params })}\n`);
  });
  return promise;
}

const address = process.env["OFAC_MCP_BRIDGE"];
if (address === undefined || address === "") {
  process.stderr.write("ofac mcp shim: OFAC_MCP_BRIDGE not set\n");
  process.exit(2);
} else {
  const server = new Server(
    { name: "ofac-ask-user", version: "1.0.0" },
    { capabilities: { tools: {} } },
  );
  server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: [TOOL] }));
  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    if (request.params.name !== "ask_user") {
      throw new Error(`unknown tool '${request.params.name}'`);
    }
    const args =
      typeof request.params.arguments === "object" && request.params.arguments !== null
        ? (request.params.arguments as Record<string, unknown>)
        : {};
    const question = textArg(args["question"]);
    if (question === undefined || question === "") {
      throw new Error("ask_user requires a non-empty question");
    }
    const choices = stringArray(args["choices"]);
    const answer = await askBridge(address, {
      question,
      ...(choices !== undefined ? { choices } : {}),
    });
    return {
      content: [{ type: "text" as const, text: answer }],
      structuredContent: { answer },
    };
  });
  await server.connect(new StdioServerTransport());
}
