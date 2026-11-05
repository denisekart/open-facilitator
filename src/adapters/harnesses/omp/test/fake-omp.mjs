// fake-omp: a stand-in for `omp --mode rpc` speaking the EXACT v18.12.6
// probe protocol (local://omp-rpc-probe.md): ready frame first, response
// acks keyed by request id, the agent event stream, prompt_result terminal.
//
// Scenario switches (env):
//   OFAC_FAKE_OMP_ARGV_FILE  write {argv, resume, prompt, steers, mcp,
//                                  systemPrompt, flags} JSON on exit (assertion surface)
//   OFAC_FAKE_OMP_MODE       "fail" → stderr + exit 3 right after the prompt ack
//   OFAC_FAKE_OMP_SESSION    the get_state sessionId (default generated)
// Objective markers (prompt text):
//   ASKME                    → perform a REAL ask_user MCP round-trip: read the
//                              harness-generated .mcp.json from the --cwd dir,
//                              spawn the shim exactly like omp does, speak JSON-RPC
//                              stdio (initialize → tools/list → tools/call), embed
//                              the answer in the final assistant text
//   HOLD                     → dwell after the ack before emitting the stream
//                              (mid-run steer/kill window; OFAC_FAKE_OMP_HOLD_MS widens it)
// Stdin frames: {id,type:"prompt",message}, {id,type:"get_state"},
// {type:"steer",message}, {type:"abort"} (→ exit 0, no terminal frame).
import { spawn } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";

const summary = {
  argv: process.argv.slice(2),
  resume: null,
  prompt: null,
  systemPrompt: null,
  steers: [],
  mcp: { configSeen: false, bridge: null, tools: [], answer: null, error: null },
  flags: {},
};
for (let i = 0; i < summary.argv.length; i += 1) {
  const a = summary.argv[i];
  if (a === "--resume") summary.resume = summary.argv[i + 1] ?? null;
  if (a === "--cwd") summary.flags.cwd = summary.argv[i + 1] ?? null;
  if (a === "--session-dir") summary.flags.sessionDir = summary.argv[i + 1] ?? null;
  if (a === "--model") summary.flags.model = summary.argv[i + 1] ?? null;
  if (a === "--append-system-prompt") summary.systemPrompt = summary.argv[i + 1] ?? null;
}
summary.flags.modeRpc = summary.argv.includes("--mode") && summary.argv.includes("rpc");
summary.flags.noUi = summary.argv.includes("--no-ui");
summary.flags.autoApprove = summary.argv.includes("--auto-approve");

const out = (obj) => process.stdout.write(`${JSON.stringify(obj)}\n`);

let shim = null;
const flushSummary = () => {
  const file = process.env["OFAC_FAKE_OMP_ARGV_FILE"];
  if (file !== undefined) {
    try {
      writeFileSync(file, JSON.stringify(summary));
    } catch {
      /* best-effort assertion surface */
    }
  }
};
process.on("exit", flushSummary);

out({
  type: "ready",
  protocolVersion: 1,
  supportedProtocolVersions: [1, 2],
  maxFrameBytes: 1048576,
  maxReassembledFrameBytes: 67108864,
});

let sessionId = process.env["OFAC_FAKE_OMP_SESSION"] ?? `fake-sess-${Date.now().toString(36)}`;
if (summary.resume !== null) sessionId = `resumed-${summary.resume}`;

function mcpRoundTrip(question, choices) {
  return new Promise((resolve, reject) => {
    const dir = summary.flags.cwd ?? process.cwd();
    const configPath = `${dir}/.mcp.json`;
    if (!existsSync(configPath)) {
      reject(new Error(".mcp.json not auto-discoverable"));
      return;
    }
    summary.mcp.configSeen = true;
    let entry;
    try {
      const doc = JSON.parse(readFileSync(configPath, "utf8"));
      entry = doc.mcpServers.ofac;
    } catch (error) {
      reject(error);
      return;
    }
    summary.mcp.bridge = entry.env?.OFAC_MCP_BRIDGE ?? null;
    const child = spawn(entry.command, entry.args, {
      cwd: dir,
      env: { ...process.env, ...(entry.env ?? {}) },
      stdio: ["pipe", "pipe", "pipe"],
    });
    shim = child;
    child.stderr.on("data", () => undefined);
    child.on("error", reject);
    let buf = "";
    const pending = new Map();
    child.stdout.on("data", (chunk) => {
      buf += chunk.toString("utf8");
      for (;;) {
        const nl = buf.indexOf("\n");
        if (nl < 0) break;
        const line = buf.slice(0, nl).trim();
        buf = buf.slice(nl + 1);
        if (line === "") continue;
        let msg;
        try {
          msg = JSON.parse(line);
        } catch {
          continue;
        }
        if (msg.id !== undefined && pending.has(msg.id)) {
          pending.get(msg.id)(msg);
          pending.delete(msg.id);
        }
      }
    });
    const call = (frame, id) =>
      new Promise((res) => {
        if (id !== undefined) pending.set(id, res);
        child.stdin.write(`${JSON.stringify(frame)}\n`);
        if (id === undefined) res({});
      });
    const guard = setTimeout(() => finish(new Error("mcp round-trip timeout")), 25_000);
    const finish = (error, answer) => {
      clearTimeout(guard);
      child.kill("SIGTERM");
      shim = null;
      if (error !== undefined && error !== null) reject(error);
      else resolve(answer);
    };
    (async () => {
      const init = await call(
        {
          jsonrpc: "2.0",
          id: 1,
          method: "initialize",
          params: {
            protocolVersion: "2025-03-26",
            capabilities: {},
            clientInfo: { name: "fake-omp", version: "1" },
          },
        },
        1,
      );
      if (init.error !== undefined) throw new Error(`initialize: ${JSON.stringify(init.error)}`);
      child.stdin.write(
        `${JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" })}\n`,
      );
      const tools = await call({ jsonrpc: "2.0", id: 2, method: "tools/list" }, 2);
      summary.mcp.tools = (tools.result?.tools ?? []).map((t) => t.name);
      if (!summary.mcp.tools.includes("ask_user")) {
        throw new Error(`ask_user absent: ${JSON.stringify(summary.mcp.tools)}`);
      }
      const args = { question };
      if (choices !== undefined) args.choices = choices;
      const call_ = await call(
        {
          jsonrpc: "2.0",
          id: 3,
          method: "tools/call",
          params: { name: "ask_user", arguments: args },
        },
        3,
      );
      const text = (call_.result?.content ?? []).find((c) => c.type === "text")?.text;
      if (text === undefined)
        throw new Error(`tool error: ${JSON.stringify(call_.error ?? call_)}`);
      finish(null, text);
    })().catch((error) => finish(error instanceof Error ? error : new Error(String(error))));
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let buf = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => {
  buf += chunk;
  for (;;) {
    const nl = buf.indexOf("\n");
    if (nl < 0) break;
    const line = buf.slice(0, nl).trim();
    buf = buf.slice(nl + 1);
    if (line === "") continue;
    let frame;
    try {
      frame = JSON.parse(line);
    } catch {
      continue;
    }
    handle(frame).catch((error) => {
      process.stderr.write(`fake-omp: ${String(error)}\n`);
      process.exit(4);
    });
  }
});
process.stdin.on("end", () => process.exit(0)); // cooperative: harness closed stdin

async function handle(frame) {
  if (frame.type === "steer") {
    summary.steers.push(frame.message);
    return;
  }
  if (frame.type === "abort") {
    shim?.kill("SIGTERM");
    process.exit(0); // cancel law: NO terminal frame
  }
  if (frame.type === "get_state") {
    out({
      id: frame.id,
      type: "response",
      command: "get_state",
      success: true,
      data: { sessionId, state: "idle" },
    });
    return;
  }
  if (frame.type !== "prompt") return;
  summary.prompt = frame.message;
  out({ id: frame.id, type: "response", command: "prompt", success: true });
  if (process.env["OFAC_FAKE_OMP_MODE"] === "fail") {
    process.stderr.write("fake omp exploded\n");
    process.exit(3);
  }
  out({ type: "agent_start" });
  out({ type: "turn_start" });
  out({ type: "message_start", role: "user" });
  out({ type: "message_end", role: "user", content: [{ type: "text", text: frame.message }] });
  out({ type: "message_start", role: "assistant" });
  out({
    type: "message_end",
    role: "assistant",
    content: [{ type: "toolCall", name: "bash", args: { cmd: "ls" } }],
  });
  const holdMs = Number(process.env["OFAC_FAKE_OMP_HOLD_MS"] ?? 1200);
  if (process.env["OFAC_FAKE_OMP_HOLD"] === "1" || String(frame.message).includes("HOLD")) {
    await sleep(holdMs);
  }
  let answer = null;
  if (String(frame.message).includes("ASKME")) {
    try {
      answer = await mcpRoundTrip("which framework?", ["postgres", "sqlite"]);
      summary.mcp.answer = answer;
    } catch (error) {
      summary.mcp.error = String(error);
    }
  }
  const parts = ["Done."];
  if (answer !== null) parts.push(`answer: ${answer}`);
  if (summary.mcp.error !== null) parts.push(`ask-failed: ${summary.mcp.error}`);
  for (const s of summary.steers) parts.push(`steered: ${s}`);
  const finalText = parts.join(" ");
  out({
    type: "message_end",
    role: "assistant",
    content: [
      { type: "text", text: finalText },
      { type: "toolCall", name: "ask_user", args: { question: "which framework?" } },
    ],
  });
  out({ type: "turn_end", message: finalText });
  out({ type: "agent_end", messages: [finalText] });
  out({
    type: "prompt_result",
    id: frame.id,
    agentInvoked: true,
    status: "completed",
    sessionSettled: true,
  });
  out({ type: "session_settled" });
  // Stay alive until the harness closes stdin (cooperative shutdown).
}
