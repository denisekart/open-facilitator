// Package surface: both entry points import cleanly from the SOURCE tree
// (the built dist is smoke-tested by the build step itself).
import { describe, expect, it } from "vitest";
import {
  buildEvent,
  createEchoHandler,
  createLinkClient,
  createStubHandler,
  dedupeKeyOf,
  enroll,
  FaultInjector,
  LINK_PACKAGE,
  MAX_ENVELOPE_BYTES,
  MemoryLedger,
  ProtocolMismatchError,
  SessionSupersededError,
  TransportError,
} from "../src/index.js";
import {
  digestOfManifest,
  HttpPollTransport,
  postCapabilities,
  SignalrTransport,
  SqliteLedger,
} from "../src/node/index.js";
import { parseArgsv, ConfigError } from "../src/bin/linkd.js";

describe("workspace-link package surface", () => {
  it("exports the SDK core from '.'", () => {
    expect(LINK_PACKAGE).toBe("@ofac/workspace-link");
    for (const value of [
      buildEvent,
      createEchoHandler,
      createLinkClient,
      createStubHandler,
      dedupeKeyOf,
      enroll,
      FaultInjector,
      MemoryLedger,
      ProtocolMismatchError,
      SessionSupersededError,
      TransportError,
    ]) {
      expect(typeof value).toBe("function");
    }
    expect(MAX_ENVELOPE_BYTES).toBe(262144);
  });

  it("exports the node layer from './node'", () => {
    for (const value of [
      digestOfManifest,
      HttpPollTransport,
      postCapabilities,
      SignalrTransport,
      SqliteLedger,
    ]) {
      expect(typeof value).toBe("function");
    }
    expect(digestOfManifest("{}")).toMatch(/^sha256:[0-9a-f]{64}$/);
  });

  it("linkd CLI parses and reports config errors", () => {
    expect(() => parseArgsv(["run", "--url", "http://x", "--secret", "s"])).toThrow(/--endpoint/);
    expect(() => parseArgsv(["run"])).toThrow(ConfigError);
    const config = parseArgsv([
      "run",
      "--url",
      "http://x",
      "--token",
      "t",
      "--endpoint",
      "gw",
      "--team",
      "alpha",
      "--manifest",
      "m.json",
    ]);
    expect(config).toMatchObject({
      endpointId: "gw",
      team: "alpha",
      transport: "signalr",
      mode: "echo",
      trustMode: "isolated-executor",
      statePath: "ofac-linkd.sqlite3",
    });
  });
});
