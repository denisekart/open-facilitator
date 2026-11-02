// Team policy engine + loader + audit line. The matrix
// covers capability × resource × actorAnyOf × ordering × deny-by-default,
// malformed-file boot failure, revision-hash stability, group-term
// normalization, and the `role:`-denies law.
import { createHash } from "node:crypto";

import type { CapabilityDescriptor } from "@ofac/protocol";
import { generateKeyPair, SignJWT, type CryptoKey } from "jose";
import { describe, expect, it } from "vitest";

import { auditPolicy } from "../../src/audit.js";
import { extractClaims } from "../../src/claims.js";
import { loadPolicyFile, PolicyFileError } from "../../src/policy-file.js";
import { DefaultPolicy, TeamPolicy, type PolicyContext } from "../../src/policy.js";

const ANALYZE: CapabilityDescriptor = {
  id: "research.analyze",
  version: 1,
  execution: "asynchronous",
  supportsSteering: true,
  supportsCancellation: true,
};
const PUBLISH: CapabilityDescriptor = {
  id: "research.publish",
  version: 1,
  execution: "asynchronous",
};
const FLEET = new Map<string, CapabilityDescriptor>([
  [ANALYZE.id, ANALYZE],
  [PUBLISH.id, PUBLISH],
]);

const POLICY_YAML = `
owner: team-alpha
policies:
  - capability: research.analyze
    resources: ["*"]
    actorAnyOf: []
    effect: allow
  - capability: research.publish
    resources: ["repository:svc-*"]
    actorAnyOf: ["group:/teams/team-alpha"]
    effect: confirm
  - capability: research.publish
    resources: ["*"]
    actorAnyOf: []
    effect: deny
`;

function ctx(over: Partial<PolicyContext>): PolicyContext {
  return {
    commandType: "org.facilitator.command.work.assign.v1",
    capabilityId: "research.analyze",
    childWorkspaceId: "gw.t/c1",
    resource: "research.analyze",
    claims: { subject: "workload:gw.t", groups: [], verified: false },
    workloadActor: "workload:gw.t",
    ...over,
  };
}

function groupClaims(groups: readonly string[], username?: string): PolicyContext["claims"] {
  return {
    subject: "oidc:https://idp|sub-1",
    groups,
    verified: true,
    ...(username !== undefined ? { preferredUsername: username } : {}),
  };
}

describe("loadPolicyFile", () => {
  it("loads the spec-shaped document and derives a stable revision", () => {
    const loaded = loadPolicyFile(POLICY_YAML);
    expect(loaded.file.owner).toBe("team-alpha");
    expect(loaded.file.policies).toHaveLength(3);
    expect(loaded.file.policies[1]?.effect).toBe("confirm");
    const hex = createHash("sha256").update(POLICY_YAML, "utf8").digest("hex");
    expect(loaded.revision).toBe(`sha256:${hex}`);
    expect(loaded.policyId).toBe(`team-alpha/${hex.slice(0, 12)}`);
  });

  it("is whitespace-sensitive: one space shifts the revision", () => {
    const a = loadPolicyFile(POLICY_YAML);
    const b = loadPolicyFile(POLICY_YAML.replace("owner: team-alpha", "owner:  team-alpha"));
    expect(a.revision).not.toBe(b.revision);
  });

  it("rejects malformed documents with a pointer-carrying error", () => {
    expect(() => loadPolicyFile("owner: team-alpha\npolicies: []\n")).toThrow(PolicyFileError);
    expect(() =>
      loadPolicyFile(
        "owner: t\npolicies:\n  - capability: c\n    resources: ['*']\n    actorAnyOf: []\n    effect: maybe\n",
      ),
    ).toThrow(/effect/);
    expect(() =>
      loadPolicyFile(
        "owner: t\npolicies:\n  - capability: c\n    resources: ['*']\n    actorAnyOf: []\n    effect: allow\n    typo: 1\n",
      ),
    ).toThrow(/additional propert/i);
    expect(() => loadPolicyFile("owner: t\npolicies: 7\n")).toThrow(PolicyFileError);
    expect(() => loadPolicyFile("\t- : [")).toThrow(PolicyFileError);
  });
});

describe("TeamPolicy matrix", () => {
  const policy = new TeamPolicy(loadPolicyFile(POLICY_YAML), FLEET);

  it("allows a wildcard row for the advertised capability", () => {
    expect(policy.evaluate("command.forward", ctx({}))).toBe("allow");
  });

  it("confirms a publish on a prefix-glob resource with a group-matched actor", () => {
    expect(
      policy.evaluate(
        "command.forward",
        ctx({
          capabilityId: "research.publish",
          resource: "repository:svc-core",
          claims: groupClaims(["/teams/team-alpha"]),
        }),
      ),
    ).toBe("confirm");
  });

  it("matches group terms raw, /teams/-stripped, and mirrored", () => {
    const c = (groups: readonly string[]): PolicyVerdictLike =>
      policy.evaluate(
        "command.forward",
        ctx({
          capabilityId: "research.publish",
          resource: "repository:svc-x",
          claims: groupClaims(groups),
        }),
      );
    expect(c(["/teams/team-alpha"])).toBe("confirm");
    expect(c(["team-alpha"])).toBe("confirm");
    // a group from another team falls through to the deny row
    expect(c(["/teams/team-beta"])).toBe("deny");
  });

  type PolicyVerdictLike = "allow" | "deny" | "confirm";

  it("unverified claims get no group privilege — first-match falls to deny", () => {
    expect(
      policy.evaluate(
        "command.forward",
        ctx({ capabilityId: "research.publish", resource: "repository:svc-core" }),
      ),
    ).toBe("deny");
  });

  it("denies an out-of-glob resource via the wildcard deny row", () => {
    expect(
      policy.evaluate(
        "command.forward",
        ctx({
          capabilityId: "research.publish",
          resource: "repository:other",
          claims: groupClaims(["team-alpha"]),
        }),
      ),
    ).toBe("deny");
  });

  it("user: terms match a verified preferred_username", () => {
    const p = new TeamPolicy(
      loadPolicyFile(
        "owner: t\npolicies:\n  - capability: research.publish\n    resources: ['*']\n    actorAnyOf: ['user:alice']\n    effect: allow\n",
      ),
      FLEET,
    );
    expect(
      p.evaluate(
        "command.forward",
        ctx({ capabilityId: "research.publish", claims: groupClaims([], "alice") }),
      ),
    ).toBe("allow");
    expect(
      p.evaluate(
        "command.forward",
        ctx({ capabilityId: "research.publish", claims: groupClaims([], "bob") }),
      ),
    ).toBe("deny");
  });

  it("role: rows deny — the realm mints no roles today", () => {
    const p = new TeamPolicy(
      loadPolicyFile(
        "owner: t\npolicies:\n  - capability: research.analyze\n    resources: ['*']\n    actorAnyOf: ['role:team-maintainer']\n    effect: allow\n",
      ),
      FLEET,
    );
    expect(p.evaluate("command.forward", ctx({ claims: groupClaims(["team-alpha"]) }))).toBe(
      "deny",
    );
  });

  it("deny-by-default: a capability with no row is denied despite being advertised", () => {
    const p = new TeamPolicy(
      loadPolicyFile(
        "owner: t\npolicies:\n  - capability: research.analyze\n    resources: ['*']\n    actorAnyOf: []\n    effect: allow\n",
      ),
      FLEET,
    );
    expect(p.evaluate("command.forward", ctx({ capabilityId: "research.publish" }))).toBe("deny");
  });

  it("fleet-honesty pre-pass survives: unadvertised denies, steer/cancel flags deny", () => {
    expect(policy.evaluate("command.forward", ctx({ capabilityId: "no.such" }))).toBe("deny");
    expect(
      policy.evaluate(
        "command.forward",
        ctx({
          commandType: "org.facilitator.command.work.steer.v1",
          capabilityId: "research.publish",
        }),
      ),
    ).toBe("deny");
    expect(
      policy.evaluate(
        "command.forward",
        ctx({ commandType: "org.facilitator.command.work.steer.v1" }),
      ),
    ).toBe("allow");
  });

  it("event.forward always passes the pre-pass", () => {
    expect(policy.evaluate("event.forward", ctx({}))).toBe("allow");
  });
});

describe("DefaultPolicy fallback", () => {
  const policy = new DefaultPolicy(FLEET);
  it("ignores rows: behavior is advertence-only, verdicts unchanged", () => {
    expect(policy.evaluate("command.forward", ctx({ capabilityId: "research.publish" }))).toBe(
      "allow",
    );
    expect(
      policy.evaluate(
        "command.forward",
        ctx({ capabilityId: "research.publish", claims: groupClaims(["x"]) }),
      ),
    ).toBe("allow");
  });
});

describe("extractClaims", () => {
  let pair: CryptoKeyPair | undefined;
  async function fixedKey(which: "public" | "private"): Promise<CryptoKey> {
    pair ??= await generateKeyPair("RS256", { extractable: true });
    return which === "public" ? pair.publicKey : pair.privateKey;
  }

  async function signedFixed(claims: Record<string, unknown>): Promise<string> {
    return await new SignJWT(claims)
      .setProtectedHeader({ alg: "RS256", kid: "k1" })
      .setIssuer("http://idp/realms/f")
      .setAudience("facilitator-api")
      .setIssuedAt()
      .setExpirationTime("5m")
      .sign(await fixedKey("private"));
  }

  it("non-oidc schemes yield workload-only claims", async () => {
    const claims = await extractClaims("identity-evidence:e2e", {
      jwks: () => async () => (await fixedKey("public")) as CryptoKey,
      audience: "facilitator-api",
      workloadActor: "workload:gw.t",
    });
    expect(claims).toEqual({
      subject: "workload:gw.t",
      groups: [],
      verified: false,
      preferredUsername: undefined,
    });
  });

  it("a verifying delegation JWT yields subject + groups", async () => {
    const jwt = await signedFixed({
      sub: "alice-sub",
      delegation: true,
      groups: ["/teams/team-alpha"],
      preferred_username: "alice",
    });
    const claims = await extractClaims(`oidc:${jwt}`, {
      jwks: () => async () => (await fixedKey("public")) as CryptoKey,
      audience: "facilitator-api",
      workloadActor: "workload:gw.t",
    });
    expect(claims.verified).toBe(true);
    expect(claims.subject).toBe("oidc:http://idp/realms/f|alice-sub");
    expect(claims.groups).toEqual(["/teams/team-alpha"]);
    expect(claims.preferredUsername).toBe("alice");
  });

  it("missing delegation or a foreign audience degrades to workload", async () => {
    const deps = {
      jwks: () => async () => (await fixedKey("public")) as CryptoKey,
      audience: "facilitator-api",
      workloadActor: "workload:gw.t",
    };
    const noDelegation = await signedFixed({ sub: "a", groups: ["/teams/team-alpha"] });
    const wrongAud = await new SignJWT({ sub: "a", delegation: true })
      .setProtectedHeader({ alg: "RS256", kid: "k1" })
      .setIssuer("http://idp/realms/f")
      .setAudience("someone-else")
      .setIssuedAt()
      .setExpirationTime("5m")
      .sign(await fixedKey("private"));
    expect((await extractClaims(`oidc:${noDelegation}`, deps)).verified).toBe(false);
    expect((await extractClaims(`oidc:${wrongAud}`, deps)).verified).toBe(false);
    expect((await extractClaims("oidc:not-a-jwt", deps)).verified).toBe(false);
  });
});

describe("auditPolicy", () => {
  it("emits one authorization.evaluated line with the §12.2 field set", () => {
    const lines: { msg: string; fields?: Record<string, unknown> | undefined }[] = [];
    const logger = {
      debug: () => undefined,
      info: (msg: string, fields?: Record<string, unknown>) => lines.push({ msg, fields }),
      warn: () => undefined,
      error: () => undefined,
    };
    auditPolicy(logger, {
      decision: "confirm",
      reason: "policy-confirm",
      workId: "w-1",
      assignmentId: "a-1",
      capabilityId: "research.publish",
      resource: "repository:svc-core",
      callerWorkload: "workload:gw.src",
      gatewayWorkload: "workload:gw.t",
      humanSubject: "oidc:http://idp|sub",
      policyId: "team-alpha/abcdef012345",
      evidenceRef: "oidc:jwt…",
    });
    expect(lines).toHaveLength(1);
    const [line] = lines;
    expect(line?.msg).toBe("authorization.evaluated");
    expect(line?.fields).toMatchObject({
      decision: "confirm",
      policyId: "team-alpha/abcdef012345",
      humanSubject: "oidc:http://idp|sub",
      callerWorkload: "workload:gw.src",
    });
  });
});
