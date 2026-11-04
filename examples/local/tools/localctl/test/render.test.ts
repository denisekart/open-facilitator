// The render law as the unit gate: deterministic per-topology bundles,
// snapshot-compared. sim has no caddy/tailscale and plain loopback ports;
// lan renders `tls internal`; a digest roles file renders `image:` and
// nothing else; a missing secret names the key.
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it, beforeAll, afterAll } from "vitest";
import { parse as parseYaml } from "yaml";
import { render, EXAMPLE_DIR, type RenderResult } from "../src/render.js";
import { parseRoles, loadRoles } from "../src/env.js";

// Deterministic secrets fixture (the file itself is gitignored).
const FIXTURE = [
  "OFAC_ENROLLMENT_TOKEN=static-break-glass",
  "POSTGRES_PASSWORD=pw",
  "S3_ACCESS_KEY=ofac-admin",
  "S3_SECRET_KEY=s3cret",
  "IDP_CLI_SECRET=cli-secret",
  "KEYCLOAK_ADMIN_PASSWORD=kcpw",
  "CHILD_SECRETS_HOME=child-home",
].join("\n");

function writeFixture(name: string, content: string): string {
  const file = path.join(EXAMPLE_DIR, "secrets", name);
  mkdirSync(path.dirname(file), { recursive: true });
  writeFileSync(file, `${content}\n`);
  return file;
}

function cleanup(envName: string): void {
  rmSync(path.join(EXAMPLE_DIR, ".out", envName), { recursive: true, force: true });
  rmSync(path.join(EXAMPLE_DIR, "envs", `${envName}.lock.json`), { force: true });
}

function readBox(result: RenderResult, box: string, file: string): string {
  return readFileSync(path.join(result.outDir, box, file), "utf8");
}

describe("render: sim", () => {
  let result: RenderResult;

  beforeAll(async () => {
    writeFixture("sim.env", FIXTURE);
    writeFixture("example.env", FIXTURE);
    result = await render(loadRoles(EXAMPLE_DIR, "sim"));
  });
  afterAll(() => {
    rmSync(path.join(EXAMPLE_DIR, ".out", "sim"), { recursive: true, force: true });
  });

  it("kernel compose snapshots clean", () => {
    expect(readBox(result, "kernel", "compose.yaml")).toMatchSnapshot();
  });

  it("drops caddy and tailscale entirely", () => {
    const compose = readBox(result, "kernel", "compose.yaml");
    expect(compose).not.toMatch(/caddy/i);
    expect(compose).not.toMatch(/tailscale/i);
  });

  it("publishes plain loopback ports", () => {
    const compose = readBox(result, "kernel", "compose.yaml");
    for (const p of [
      '"127.0.0.1:7488:8080"',
      // sim alias law: the issuer rides :7480, so Keycloak LISTENS there
      // (KC_HTTP_PORT) and the map is same-port same-port.
      '"127.0.0.1:7480:7480"',
      '"127.0.0.1:7490:18888"',
      '"127.0.0.1:7091:18890"',
    ])
      expect(compose).toContain(p);
  });

  it("rides the external shared network", () => {
    const doc = parseYaml(readBox(result, "kernel", "compose.yaml")) as {
      networks: Record<string, { name: string; external: boolean }>;
    };
    expect(doc.networks["default"]).toEqual({ name: "ofac-local", external: true });
    const node = parseYaml(readBox(result, "pc1", "compose.yaml")) as typeof doc;
    expect(node.networks["default"]).toEqual({ name: "ofac-local", external: true });
  });

  it("node bundle: minted-secret placeholder, no published ports, snapshot", () => {
    const compose = readBox(result, "pc1", "compose.yaml");
    expect(compose).toContain("OFAC_ENROLL_SECRET:?");
    expect(compose).not.toMatch(/^\s+ports:/m);
    expect(compose).toMatchSnapshot();
    // team child secret resolved to the per-team key
    expect(compose).toContain("OFAC_CHILD_SECRET: ${CHILD_SECRETS_HOME:?");
  });

  it("realm + identity config carry the sim issuer byte-identically", () => {
    const issuer = "http://id.localhost:7480/realms/facilitator";
    const cfg = JSON.parse(readBox(result, "kernel", "facilitator.json")) as {
      identity: { oidc: { authority: string } };
    };
    expect(cfg.identity.oidc.authority).toBe(issuer);
    expect(readBox(result, "kernel", "compose.yaml")).toContain(
      `KC_HOSTNAME: http://id.localhost:${"7480"}`,
    );
    // The kernel fetches JWKS by the issuer host INSIDE the spine: the
    // service must listen on the issuer port and own the alias there.
    expect(readBox(result, "kernel", "compose.yaml")).toContain('KC_HTTP_PORT: "7480"');
    expect(readBox(result, "kernel", "compose.yaml")).toContain("aliases: [id.localhost]");
    const realm = JSON.parse(readBox(result, "kernel", path.join("keycloak", "realm.json"))) as {
      clients: { clientId: string; redirectUris?: string[] }[];
    };
    expect(realm.clients.find((c) => c.clientId === "facilitator-api")?.redirectUris).toContain(
      "http://localhost:7488/*",
    );
    expect(readBox(result, "kernel", path.join("keycloak", "realm.json"))).toContain("cli-secret");
  });

  it("kernel env file carries the full contract incl. $$-indirects", () => {
    const env = readBox(result, "kernel", "facilitator.env");
    expect(env).toContain("Link__EnrollmentToken=static-break-glass");
    expect(env).toContain("Storage__S3__ForcePathStyle=true");
    expect(env).toContain(
      "Provision__Workloads__0__Env__OFAC_ENROLL_SECRET=$$OFAC_ENROLLMENT_TOKEN",
    );
    expect(env).toContain(
      "Provision__Workloads__0__Labels__io.facilitator.managed=kernel-provisioned",
    );
    expect(env).toContain("Provision__LauncherEndpoint=launcher.pc1");
  });

  it("lock records the local tags deterministically", () => {
    const lock = JSON.parse(
      readFileSync(path.join(EXAMPLE_DIR, "envs", "sim.lock.json"), "utf8"),
    ) as {
      resolved: Record<string, string>;
      history: { timestamp: string }[];
    };
    expect(lock.resolved).toEqual({
      kernel: "ofac-facilitator:local",
      node: "ofac-node-runtime:local",
      worker: "ofac-elastic-worker:local",
    });
    for (const h of lock.history) expect(h.timestamp).toBe("1970-01-01T00:00:00.000Z");
  });
});

describe("render: lan topology", () => {
  let result: RenderResult;

  beforeAll(async () => {
    writeFixture("sim.env", FIXTURE);
    const raw = JSON.parse(
      readFileSync(path.join(EXAMPLE_DIR, "envs", "example.json"), "utf8"),
    ) as Record<string, unknown>;
    raw["name"] = "lan-test";
    raw["topology"] = "lan";
    delete raw["tailscale"];
    const roles = parseRoles(raw, "lan-test");
    result = await render(roles);
  });
  afterAll(() => cleanup("lan-test"));

  it("renders a single 443 site with tls internal", () => {
    expect(readBox(result, "kernel", "Caddyfile")).toMatchSnapshot();
    const compose = readBox(result, "kernel", "compose.yaml");
    expect(compose).toContain("caddy:");
    expect(compose).toContain('"443:443"');
    // the header comment may legitimately mention tailscale; the SERVICE must not exist
    expect(compose).not.toMatch(/^\s+tailscale:\s*$/m);
    expect(compose).not.toMatch(/image:.*tailscale/);
    // no loopback publishes outside sim — Caddy owns the surface
    expect(compose).not.toContain("127.0.0.1:7488");
  });

  it("tailscale topology adds the sidecar + funnel", async () => {
    const raw = JSON.parse(
      readFileSync(path.join(EXAMPLE_DIR, "envs", "example.json"), "utf8"),
    ) as Record<string, unknown>;
    raw["name"] = "ts-test";
    const roles = parseRoles(raw, "ts-test");
    const ts = await render(roles);
    try {
      const compose = readBox(ts, "kernel", "compose.yaml");
      expect(compose).toContain("tailscale/tailscale:");
      expect(compose).toContain("TS_AUTHKEY: ${TS_AUTHKEY:?");
      expect(compose).toContain("TS_ENABLE_FUNNEL");
    } finally {
      cleanup("ts-test");
    }
  });
});

describe("render: fail-closed law", () => {
  it("digest-pinned images render `image:` and nothing else", async () => {
    writeFixture("sim.env", FIXTURE);
    const raw = JSON.parse(
      readFileSync(path.join(EXAMPLE_DIR, "envs", "sim.json"), "utf8"),
    ) as Record<string, unknown>;
    raw["name"] = "digest-test";
    raw["images"] = {
      kernel:
        "ghcr.io/denisekart/open-facilitator@sha256:0000000000000000000000000000000000000000000000000000000000000000",
      node: "ghcr.io/denisekart/open-facilitator-node@sha256:1111111111111111111111111111111111111111111111111111111111111111",
      worker:
        "ghcr.io/denisekart/open-facilitator-worker@sha256:2222222222222222222222222222222222222222222222222222222222222222",
    };
    const roles = parseRoles(raw, "digest-test");
    const result = await render(roles);
    try {
      const kernel = readBox(result, "kernel", "compose.yaml");
      expect(kernel).toContain("image: ghcr.io/denisekart/open-facilitator@sha256:0000");
      expect(kernel).not.toMatch(/^\s+build:/m);
      const node = readBox(result, "pc1", "compose.yaml");
      expect(node).toContain("image: ghcr.io/denisekart/open-facilitator-node@sha256:1111");
      expect(node).not.toMatch(/^\s+build:/m);
      expect(readBox(result, "pc1", ".env")).toContain(
        "WORKER_IMAGE=ghcr.io/denisekart/open-facilitator-worker@sha256:2222",
      );
    } finally {
      cleanup("digest-test");
    }
  });

  it("a missing secret key fails naming the key", async () => {
    writeFixture(
      "missing-test.env",
      FIXTURE.split("\n")
        .filter((l) => !l.startsWith("S3_ACCESS_KEY="))
        .join("\n"),
    );
    const raw = JSON.parse(
      readFileSync(path.join(EXAMPLE_DIR, "envs", "sim.json"), "utf8"),
    ) as Record<string, unknown>;
    raw["name"] = "missing-test";
    raw["secretsFile"] = "secrets/missing-test.env";
    const roles = parseRoles(raw, "missing-test");
    try {
      await expect(render(roles)).rejects.toThrow(/S3_ACCESS_KEY/);
    } finally {
      cleanup("missing-test");
      rmSync(path.join(EXAMPLE_DIR, "secrets", "missing-test.env"), { force: true });
    }
  });

  it("roles validation names the missing key", () => {
    expect(() => parseRoles({ topology: "sim" }, "x")).toThrow(/x\.kernel:\? missing/);
    expect(() =>
      parseRoles(
        {
          ...JSON.parse(readFileSync(path.join(EXAMPLE_DIR, "envs", "sim.json"), "utf8")),
          kernel: { host: "f", teams: ["home"] },
          nodes: [{ host: "p", gatewayId: "bad", teams: ["home"], launcher: true }],
        },
        "x",
      ),
    ).toThrow(/gatewayId/);
    expect(existsSync(path.join(EXAMPLE_DIR, "envs", "sim.json"))).toBe(true);
  });
});
