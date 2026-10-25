---
name: long-job-telemetry-before-relaunch
description: "Never re-launch a 20+ minute job blind: wire full telemetry into the launch or change the experiment instead"
condition: "features test"
question: "Does this tool call launch or re-run a job the conversation already shows takes 20+ minutes, while adding no new telemetry (full unfiltered log capture, phase timestamps, child-process/daemon forensics, heartbeat) compared to the previous attempt?"
scope: "tool"
---

For any job plausibly running 20+ minutes, decide before launching what evidence a timeout or stall must leave behind, and embed it in the launch command itself: redirect full unfiltered output to a versioned log file, emit timestamped phase markers (`date -u; echo PHASE=x`), snapshot child process trees and daemon health (`docker events`, `ps --ppid`) in the same command, and bound it so partial evidence survives the kill. Never pipe a long suite straight into a narrowing `grep`/`tail` — that discards the post-stall trail. If a prior identical-thickness attempt already timed out, re-running it unchanged is a defect: add telemetry or vary the experiment (stock control feature, single isolated scenario). One fully-instrumented hour-long run beats ten blind 30-minute reruns that cannot answer where they stalled.