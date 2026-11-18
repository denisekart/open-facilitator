// Proof-of-possession wire: build → parse → verify round-trip, and every
// rejection the verifier can answer (tamper, foreign key, wrong channel,
// channel-binding, clock/expiry). The clock is frozen, so the whole detached
// JWS is pinned byte-for-byte; Ed25519 does the rest. No network, no kernel.
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import {
  buildProof,
  buildRotationProof,
  parseProof,
  signProofForGateway,
  verifyProof,
  type ProofRejection,
  type VerifyProofOptions,
} from "../../src/index.js";
import { cleanUp, makeIdentity } from "./fixtures.js";

const NOW_SECONDS = 1_764_700_000; // a frozen "now" for the whole file

beforeAll(() => {
  vi.useFakeTimers();
  vi.setSystemTime(NOW_SECONDS * 1000);
});

afterEach(() => {
  vi.setSystemTime(NOW_SECONDS * 1000);
});

afterAll(() => {
  vi.useRealTimers();
  cleanUp();
});

const identity = makeIdentity();

const base = {
  endpointId: "gw.team-alpha/node-1",
  htm: "POST",
  htu: "https://kernel.test/api/link/v1/proof",
  nonce: "7.42.deadbeef",
  seq: 42,
  epoch: 7,
} as const;

const verifyOpts: VerifyProofOptions = {
  endpointId: base.endpointId,
  htm: base.htm,
  htu: base.htu,
  publicKeyBase64Url: identity.publicKeyBase64Url,
};

describe("buildProof / parseProof", () => {
  it("round-trips every claim through the detached JWS", () => {
    const proof = buildProof(identity, { ...base, jti: "jti-1", cbh: "cbh-abc", ttlSeconds: 60 });
    const parts = proof.split(".");
    expect(parts).toHaveLength(3);
    for (const part of parts!) expect(part).toMatch(/^[A-Za-z0-9_-]+$/);

    const claims = parseProof(proof);
    expect(claims).not.toBeNull();
    expect(claims!.iss).toBe(base.endpointId);
    expect(claims!.sub).toBe(identity.keyId);
    expect(claims!.jti).toBe("jti-1");
    expect(claims!.htm).toBe("POST");
    expect(claims!.htu).toBe(base.htu);
    expect(claims!.nonce).toBe(base.nonce);
    expect(claims!.seq).toBe(42);
    expect(claims!.epoch).toBe(7);
    expect(claims!.cbh).toBe("cbh-abc");
    expect(claims!.header).toEqual({ alg: "EdDSA", typ: "ofac-proof+jwt" });
    // The frozen clock: iat = now, nbf = iat − 5 (slack), exp = iat + ttl.
    expect(claims!.iat).toBe(NOW_SECONDS);
    expect(claims!.payload["nbf"]).toBe(NOW_SECONDS - 5);
    expect(claims!.exp).toBe(NOW_SECONDS + 60);
    // The signing input is the ASCII of `b64u(header).b64u(payload)` — the
    // exact bytes the kernel re-derives.
    expect(claims!.signingInput.toString("ascii")).toBe(`${parts[0]}.${parts[1]}`);
    expect(claims!.signature).toHaveLength(64);
  });

  it("defaults jti to a fresh random id and ttl to 30s", () => {
    const a = parseProof(buildProof(identity, base))!;
    const b = parseProof(buildProof(identity, base))!;
    expect(a.jti).toMatch(/^[0-9a-f-]{36}$/); // a random UUIDv4
    expect(a.jti).not.toBe(b.jti); // frozen clock still yields fresh jti
    expect(a.cbh).toBe("");
    expect(a.exp - a.iat).toBe(30);
  });

  it("is byte-deterministic for a fixed jti/ttl pair (same key, same second)", () => {
    const fields = { ...base, jti: "fixed", ttlSeconds: 30 };
    expect(buildProof(identity, fields)).toBe(buildProof(identity, fields));
  });
});

/** A proof whose header was replaced (payload/signature kept as-is). */
function withHeader(proof: string, header: Record<string, unknown>): string {
  const [, payload, sig] = proof.split(".");
  return `${Buffer.from(JSON.stringify(header)).toString("base64url")}.${payload}.${sig}`;
}

/** A proof with payload claims changed (the signature no longer covers it). */
function withPayload(proof: string, patch: Record<string, unknown>): string {
  const [header, payloadB64, sig] = proof.split(".");
  const payload = JSON.parse(Buffer.from(payloadB64!, "base64url").toString("utf8")) as Record<
    string,
    unknown
  >;
  for (const [k, v] of Object.entries(patch)) {
    if (v === undefined) delete payload[k];
    else payload[k] = v;
  }
  return `${header}.${Buffer.from(JSON.stringify(payload)).toString("base64url")}.${sig}`;
}

describe("parseProof malformations", () => {
  const good = buildProof(identity, base);
  const [headerB64, payloadB64, sigB64] = good.split(".");
  const cases: [string, string][] = [
    ["an empty string", ""],
    ["two parts", `${headerB64}.${payloadB64}`],
    ["four parts", `${good}.extra`],
    ["an empty part", `${headerB64}.${payloadB64}.`],
    ["non-base64url characters", `${headerB64}.${payloadB64}.${sigB64}$`],
    ["a 63-byte signature", `${headerB64}.${payloadB64}.${Buffer.alloc(63).toString("base64url")}`],
    [
      "a header that is not JSON",
      `${Buffer.from("nope").toString("base64url")}.${payloadB64}.${sigB64}`,
    ],
    [
      "a payload that is not JSON",
      `${headerB64}.${Buffer.from("nope").toString("base64url")}.${sigB64}`,
    ],
    [
      "a payload that is a scalar",
      `${headerB64}.${Buffer.from("7").toString("base64url")}.${sigB64}`,
    ],
    ["a header with a non-EdDSA alg", withHeader(good, { alg: "HS256", typ: "x" })],
    ["a missing claim", withPayload(good, { cbh: undefined })],
    ["a non-numeric seq", withPayload(good, { seq: "42" })],
    ["a non-finite epoch", withPayload(good, { epoch: Number.NaN })],
  ];
  for (const [name, proof] of cases) {
    it(`returns null for ${name}`, () => {
      expect(parseProof(proof)).toBeNull();
    });
  }

  it("a header that is a JSON array is structural garbage", () => {
    expect(
      parseProof(`${Buffer.from("[1]").toString("base64url")}.${payloadB64}.${sigB64}`),
    ).toBeNull();
  });
});

describe("verifyProof", () => {
  it("accepts a fresh proof from the pinned key", () => {
    const result = verifyProof(buildProof(identity, base), verifyOpts);
    expect(result.ok).toBe(true);
    if (result.ok) expect(result.claims.nonce).toBe(base.nonce);
  });

  it("folds ws/wss to the transport they ride", () => {
    const ws = buildProof(identity, { ...base, htu: "ws://kernel.test/hubs/link" });
    expect(verifyProof(ws, { ...verifyOpts, htu: "http://kernel.test/hubs/link" }).ok).toBe(true);
    const wss = buildProof(identity, { ...base, htu: "wss://kernel.test/hubs/link" });
    expect(verifyProof(wss, { ...verifyOpts, htu: "https://kernel.test/hubs/link" }).ok).toBe(true);
    // And the proof may name http:// while the verifier dials wss://.
    const http = buildProof(identity, { ...base, htu: "https://kernel.test/hubs/link" });
    expect(verifyProof(http, { ...verifyOpts, htu: "wss://kernel.test/hubs/link" }).ok).toBe(true);
  });

  const other = makeIdentity();
  const rejections: [string, string, VerifyProofOptions, ProofRejection][] = [
    [
      "a tampered payload",
      withPayload(buildProof(identity, base), { seq: 43 }),
      verifyOpts,
      "signature",
    ],
    ["a signature from another key", buildProof(other, base), verifyOpts, "signature"],
    [
      "a foreign issuer claim",
      buildProof(identity, { ...base, endpointId: "other" }),
      verifyOpts,
      "issuer",
    ],
    ["the wrong channel verb", buildProof(identity, { ...base, htm: "WS" }), verifyOpts, "channel"],
    [
      "the wrong channel URI",
      buildProof(identity, { ...base, htu: "https://kernel.test/other" }),
      verifyOpts,
      "channel",
    ],
    [
      "a channel-binding mismatch",
      buildProof(identity, { ...base, cbh: "tls-export-1" }),
      { ...verifyOpts, cbh: "tls-export-2" },
      "channel-binding",
    ],
    ["an expired proof", buildProof(identity, { ...base, ttlSeconds: -60 }), verifyOpts, "clock"],
    [
      "a proof not yet issued (verifier clock behind it)",
      buildProof(identity, base),
      { ...verifyOpts, nowSeconds: NOW_SECONDS - 3600 },
      "clock",
    ],
    [
      "a proof past expiry (verifier clock ahead of it)",
      buildProof(identity, { ...base, ttlSeconds: 30 }),
      { ...verifyOpts, nowSeconds: NOW_SECONDS + 3600 },
      "clock",
    ],
    ["a structurally broken proof", "not.a.proof!", verifyOpts, "malformed"],
    [
      "a public key that is not 32 bytes",
      buildProof(identity, base),
      { ...verifyOpts, publicKeyBase64Url: "tooshort" },
      "signature",
    ],
  ];
  for (const [name, proof, opts, reason] of rejections) {
    it(`answers ${reason} for ${name}`, () => {
      const result = verifyProof(proof, opts);
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.reason).toBe(reason);
    });
  }

  it("an omitted cbh option accepts any binding (non-TLS transport)", () => {
    const proof = buildProof(identity, { ...base, cbh: "whatever" });
    expect(verifyProof(proof, verifyOpts).ok).toBe(true);
    expect(verifyProof(proof, { ...verifyOpts, cbh: "whatever" }).ok).toBe(true);
  });

  it("skewSeconds widens the clock window", () => {
    const proof = buildProof(identity, { ...base, ttlSeconds: -60 }); // expired 60s ago
    expect(verifyProof(proof, { ...verifyOpts, skewSeconds: 120 }).ok).toBe(true);
  });

  it("replay defence: a second verifier read of the same jti is the caller's store, not the crypto", () => {
    // Same frozen second, same jti ⇒ the exact same proof bytes: replaying it
    // verifies again, which is why the kernel fences on jti/nonce/seq, not here.
    const fields = { ...base, jti: "single-use" };
    const proof = buildProof(identity, fields);
    expect(verifyProof(proof, verifyOpts).ok).toBe(true);
    expect(verifyProof(proof, verifyOpts).ok).toBe(true);
    // The replay fence the claims carry:
    const claims = parseProof(proof)!;
    expect([claims.nonce, claims.seq, claims.jti]).toEqual(["7.42.deadbeef", 42, "single-use"]);
  });
});

describe("buildRotationProof", () => {
  it("signs the mutual-agreement payload with both kids", () => {
    const newKid = "sha256:" + "cd".repeat(32);
    const proof = buildRotationProof(identity, {
      oldKid: identity.keyId,
      newKid,
      nonce: "n-1",
      seq: 9,
    });
    const [header, payload, sig] = proof.split(".");
    expect(Buffer.from(header!, "base64url").toString("utf8")).toBe(
      JSON.stringify({ alg: "EdDSA", typ: "ofac-proof+jwt" }),
    );
    const claims = JSON.parse(Buffer.from(payload!, "base64url").toString("utf8")) as Record<
      string,
      unknown
    >;
    expect(claims).toEqual({ oldKid: identity.keyId, newKid, nonce: "n-1", seq: 9 });
    expect(Buffer.from(sig!, "base64url")).toHaveLength(64);
    // The signing input is the rotation payload, verbatim — and it is not a
    // session proof, so parseProof (structural read) refuses it.
    const signingInput = Buffer.from(`${header}.${payload}`, "ascii");
    expect(identity.sign(signingInput)).toEqual(Buffer.from(sig!, "base64url"));
    expect(parseProof(proof)).toBeNull();
  });
});

describe("signProofForGateway", () => {
  it("builds the child hello proof (htm WS, htu the gateway channel)", () => {
    const proof = signProofForGateway(identity, {
      childWorkspaceId: "gw.team-alpha/child",
      htu: "wss://gateway.test/hubs/children",
      nonce: "3.5.random",
    });
    const claims = parseProof(proof)!;
    expect(claims.iss).toBe("gw.team-alpha/child");
    expect(claims.htm).toBe("WS");
    expect(claims.htu).toBe("wss://gateway.test/hubs/children");
    expect(claims.seq).toBe(0);
    expect(claims.epoch).toBe(0);
    expect(claims.cbh).toBe("");
    // The gateway verifies it against the folded channel (wss ⇒ https):
    expect(
      verifyProof(proof, {
        endpointId: "gw.team-alpha/child",
        htm: "WS",
        htu: "https://gateway.test/hubs/children",
        publicKeyBase64Url: identity.publicKeyBase64Url,
      }).ok,
    ).toBe(true);
  });

  it("carries explicit seq/epoch/cbh when given", () => {
    const claims = parseProof(
      signProofForGateway(identity, {
        childWorkspaceId: "gw.team-alpha/child",
        htu: "wss://gateway.test/hubs/children",
        nonce: "n",
        seq: 12,
        epoch: 4,
        cbh: "cbh-9",
      }),
    )!;
    expect([claims.seq, claims.epoch, claims.cbh]).toEqual([12, 4, "cbh-9"]);
  });
});
