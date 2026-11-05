#!/usr/bin/env node
// Fake codex CLI (Track C, probe-shaped): parses `codex exec` argv, emits
// the 0.166.0 --json vocabulary on stdout (thread.started, turn.started,
// item.*, an {type:"error"} reconnect frame, non-JSON noise line,
// turn.completed), honors -o (writes the final message file) and the
// resume positionals, and — when the prompt contains ASK marker — performs
// a REAL MCP round-trip: reads $CODEX_HOME/config.toml, extracts the
// shim launch line, speaks MCP over stdio, and rides the tool's answer
// text into the final agent_message. Env knobs (fake-only, harmless to
// the real CLI): OFAC_FAKE_EXIT (exit code, default 0), OFAC_FAKE_NO_FINAL
// (skip emitting agent_message so the -o file carrier is exercised),
// OFAC_FAKE_ARGV_OUT (write the received argv as JSON), OFAC_FAKE_SLEEP_MS
// (stay alive ~this long — the cancel/kill window). Shebang + chmod +x:
// the tests point OFAC_CODEX_BIN straight at this file, like a real
// installed CLI.
import { spawn } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const args = process.argv.slice(2);
if (process.env["OFAC_FAKE_ARGV_OUT"] !== undefined) {
  writeFileSync(process.env["OFAC_FAKE_ARGV_OUT"], JSON.stringify(args));
}
if (args[0] !== "exec") {
  process.stderr.write(`fake-codex: expected 'exec' subcommand, got '${args[0] ?? ""}'\n`);
  process.exit(64);
}
let index = 1;
let resumeThread;
if (args[index] === "resume") {
  resumeThread = args[index + 1];
  index += 2;
}
const flags = [];
const positionals = [];
for (let i = index; i < args.length; i += 1) {
  const arg = args[i];
  if (arg === "-s" || arg === "-c" || arg === "-C" || arg === "-o" || arg === "--model") {
    flags.push(arg, args[(i += 1)]);
  } else if (arg === "--json" || arg === "--skip-git-repo-check") {
    flags.push(arg);
  } else {
    positionals.push(arg);
  }
}
const prompt = positionals.join(" ");
const finalPath = flags[flags.indexOf("-o") + 1];
const out = (frame) => process.stdout.write(`${JSON.stringify(frame)}\n`);

out({ type: "thread.started", thread_id: resumeThread ?? "fake-thread-1" });
out("this line is not JSON and must be ignored"); // non-JSON noise law
out({ type: "turn.started" });
out({
  type: "item.started",
  item: { id: "item-1", item_type: "command_execution", command: "ls -la" },
});
out({ type: "error", message: "Reconnecting. You can press ESC to cancel." }); // transient, non-terminal
out({
  type: "item.completed",
  item: { id: "item-1", item_type: "command_execution", command: "ls -la", exit_code: 0 },
});

let answerText = "fake codex final answer";
if (prompt.includes("ASK")) {
  // Real MCP round-trip through the worker-seeded config.toml.
  const toml = readFileSync(`${process.env["CODEX_HOME"]}/config.toml`, "utf8");
  const command = /command = "([^"]+)"/.exec(toml)?.[1] ?? "node";
  const argsMatch = /args = \[([^\]]*)\]/.exec(toml)?.[1] ?? "";
  const shimArgs = [...argsMatch.matchAll(/"([^"]*)"/g)].map((m) => m[1]);
  const envMatch = /env = \{([^}]*)\}/.exec(toml)?.[1] ?? "";
  const env = {};
  for (const m of envMatch.matchAll(/([A-Z_]+) = "([^"]*)"/g)) env[m[1]] = m[2];
  const question = /ASK\(([^)]*)\)/.exec(prompt)?.[1] ?? "which framework?";
  answerText = await callTool(command, shimArgs, env, question);
}
// Per-prompt window: a SLEEP(ms) marker in the objective keeps THIS run
// alive (the env knob would hold every run, including the completion leg).
const sleepMatch = /SLEEP\((\d+)\)/.exec(prompt);
const sleepMs =
  sleepMatch !== null
    ? Number(sleepMatch[1])
    : process.env["OFAC_FAKE_SLEEP_MS"] !== undefined
      ? Number(process.env["OFAC_FAKE_SLEEP_MS"])
      : 0;
if (sleepMs > 0) {
  await new Promise((resolve) => setTimeout(resolve, sleepMs));
}
if (process.env["OFAC_FAKE_NO_FINAL"] === undefined) {
  out({
    type: "item.completed",
    item: { id: "item-2", item_type: "agent_message", text: answerText },
  });
}
if (finalPath !== undefined) {
  // The -o carrier: the real CLI writes the final message here too.
  writeFileSync(finalPath, `${answerText} (via -o)\n`);
}
out({ type: "turn.completed", usage: { input_tokens: 1, output_tokens: 2 } });
process.exit(
  process.env["OFAC_FAKE_EXIT"] !== undefined ? Number(process.env["OFAC_FAKE_EXIT"]) : 0,
);

/** Minimal stdio MCP client: initialize, tools/list, tools/call ask_user. */
function callTool(command, childArgs, childEnv, question) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, childArgs, {
      stdio: ["pipe", "pipe", "pipe"],
      env: { ...process.env, ...childEnv },
    });
    let buffer = "";
    let step = 0;
    const send = (frame) => child.stdin.write(`${JSON.stringify(frame)}\n`);
    const fail = (what) => reject(new Error(`fake-codex mcp: ${what}`));
    child.once("error", fail);
    child.stdin.once("error", () => undefined);
    child.stderr.on("data", () => undefined);
    child.once("close", (code) => {
      if (step < 3) fail(`shim exited early (${String(code)})`);
    });
    child.stdout.on("data", (chunk) => {
      buffer += chunk.toString("utf8");
      for (;;) {
        const nl = buffer.indexOf("\n");
        if (nl < 0) break;
        const line = buffer.slice(0, nl).trim();
        buffer = buffer.slice(nl + 1);
        if (line.length === 0) continue;
        const frame = JSON.parse(line);
        if (frame.id === 1 && step === 0) {
          step = 1;
          send({ jsonrpc: "2.0", method: "notifications/initialized" });
          send({ jsonrpc: "2.0", id: 2, method: "tools/list" });
        } else if (frame.id === 2 && step === 1) {
          step = 2;
          const names = frame.result.tools.map((tool) => tool.name);
          if (!names.includes("ask_user")) fail(`ask_user missing: ${names.join(",")}`);
          send({
            jsonrpc: "2.0",
            id: 3,
            method: "tools/call",
            params: { name: "ask_user", arguments: { question, choices: ["postgres", "sqlite"] } },
          });
        } else if (frame.id === 3 && step === 2) {
          step = 3;
          if (frame.error !== undefined) fail(`tool error: ${JSON.stringify(frame.error)}`);
          const text = frame.result.content.find((part) => part.type === "text")?.text;
          child.stdin.end();
          resolve(text ?? "");
        }
      }
    });
    send({
      jsonrpc: "2.0",
      id: 1,
      method: "initialize",
      params: {
        protocolVersion: "2024-11-05",
        capabilities: {},
        clientInfo: { name: "fake-codex", version: "0.0.0" },
      },
    });
  });
}
