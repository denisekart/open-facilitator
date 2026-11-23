#!/usr/bin/env node
// The deterministic fake CLI the engine tests drive — zero AI, zero network.
// Each mode proves one playbook grammar key:
//   echo [--fail N] [--slow MS]   print a line, optionally exit N after a delay
//   prompt                        ANSI-wrapped "Proceed? [y/N]", wait for stdin,
//                                 write the answer to $FAKE_ANSWER_FILE, and only
//                                 then exit — an unmatched marker cannot fake it
//   feed                          echo the stdin it was handed at spawn
//   retry                         append an attempt marker to $FAKE_ATTEMPTS_FILE;
//                                 fail while attempts remain, then succeed
//   pty                           if stdout is a TTY, print PTY-OK (else TTY-NO)
//   report [PATH]                 write a small markdown report (PATH or stdout)
import { appendFileSync, readFileSync, writeFileSync } from "node:fs";

const mode = process.argv[2] ?? "echo";
const flags = new Set(process.argv.slice(3));
const flagValue = (name) => {
  const index = process.argv.indexOf(name);
  return index >= 0 ? process.argv[index + 1] : undefined;
};

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function main() {
  if (mode === "echo") {
    console.log("fake-tool echo ok");
    const slow = Number(flagValue("--slow") ?? 0);
    if (slow > 0) await sleep(slow);
    const fail = Number(flagValue("--fail") ?? 0);
    process.exitCode = fail;
    return;
  }
  if (mode === "prompt") {
    // ANSI-wrapped + a CR-redraw: a naive matcher sees escape bytes.
    process.stdout.write("\x1b[33m\x1b[1mworking\rProceed? [y/N] \x1b[0m");
    const answer = (await readStdin()).trim();
    const file = process.env["FAKE_ANSWER_FILE"] ?? "/dev/stdout";
    writeFileSync(file, answer);
    console.log(`answered:${answer}`);
    return;
  }
  if (mode === "feed") {
    const fed = (await readStdin()).trim();
    console.log(`fed:${fed}`);
    return;
  }
  if (mode === "retry") {
    const file = process.env["FAKE_ATTEMPTS_FILE"];
    if (file === undefined) {
      console.error("retry mode needs FAKE_ATTEMPTS_FILE");
      process.exitCode = 2;
      return;
    }
    appendFileSync(file, "x");
    const attempts = readFileSync(file, "utf8").length;
    const failUntil = Number(flagValue("--fail-until") ?? 1);
    if (attempts <= failUntil) {
      console.error(`attempt ${String(attempts)}: transient`);
      process.exitCode = 3;
      return;
    }
    console.log(`fake-tool succeeded on attempt ${String(attempts)}`);
    return;
  }
  if (mode === "pty") {
    console.log(process.stdout.isTTY === true ? "PTY-OK" : "TTY-NO");
    return;
  }
  if (mode === "report") {
    const text = "# Fake report\nObjective: whatever\n";
    const path = process.argv[3];
    if (path !== undefined) writeFileSync(path, text);
    else process.stdout.write(text);
    return;
  }
  console.error(`unknown mode '${mode}'`);
  process.exitCode = 64;
}

function readStdin() {
  return new Promise((resolve) => {
    if (process.stdin.isTTY === undefined || process.stdin.isTTY === false) {
      let data = "";
      process.stdin.setEncoding("utf8");
      process.stdin.on("data", (chunk) => {
        data += chunk;
      });
      process.stdin.on("end", () => resolve(data));
      process.stdin.resume();
    } else {
      resolve("");
    }
  });
}

await main();
