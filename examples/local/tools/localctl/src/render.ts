// Deterministic bundle renderer: roles file + secrets + versions.env in,
// per-box `.out/<env>/<box>/` bundles out (compose.yaml, .env, config,
// realm, units, supervisor, Caddyfile). Nothing in a bundle is handwritten;
// `render` is the primitive every other command rides. The resolved image
// refs land in the committed `envs/<env>.lock.json`.
import {
  copyFileSync,
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { parse as parseYaml } from "yaml";
import { readDotEnv, requireSecret, SHARED_NETWORK, type RolesFile } from "./env.js";
import { fail } from "./util.js";
import { loadLock, saveLock } from "./lock.js";
import { resolveImage } from "./registry.js";

export const HERE = path.dirname(fileURLToPath(import.meta.url));
/** examples/local — resolved by walking up from this module (the src/ AND
 * dist/src/ layouts, vitest AND built dist) to the dir that carries the
 * example's fingerprints (versions.env + envs/ + templates/). */
export const EXAMPLE_DIR = (() => {
  let dir = HERE;
  for (let up = 0; up < 8; up++) {
    if (
      existsSync(path.join(dir, "versions.env")) &&
      existsSync(path.join(dir, "envs")) &&
      existsSync(path.join(dir, "templates"))
    ) {
      return dir;
    }
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  throw new Error(`EXAMPLE_DIR: no versions.env/envs/templates above ${HERE}`);
})();
export const TEMPLATES = path.join(EXAMPLE_DIR, "templates");
export const OUT_ROOT = path.join(EXAMPLE_DIR, ".out");
/** Repo root: .out/<env>/<box> is four levels below examples/local, which
 * is one below the root — five ups from any rendered bundle directory. */
export const REPO_CONTEXT = "../../../../..";

export const LOCAL_KERNEL_IMAGE = "ofac-facilitator:local";
export const LOCAL_NODE_IMAGE = "ofac-node-runtime:local";
export const LOCAL_WORKER_IMAGE = "ofac-elastic-worker:local";

export interface Versions {
  POSTGRES_TAG: string;
  KEYCLOAK_TAG: string;
  SEAWEEDFS_TAG: string;
  AWSCLI_TAG: string;
  ASPIRE_TAG: string;
  CADDY_TAG: string;
  TAILSCALE_TAG: string;
  [digest: string]: string; // *_DIGEST pins ride along verbatim
}

const VERSION_KEYS = [
  "POSTGRES_TAG",
  "KEYCLOAK_TAG",
  "SEAWEEDFS_TAG",
  "AWSCLI_TAG",
  "ASPIRE_TAG",
  "CADDY_TAG",
  "TAILSCALE_TAG",
] as const;

export function loadVersions(): Versions {
  const raw = readDotEnv(path.join(EXAMPLE_DIR, "versions.env"));
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(raw)) if (!k.endsWith("_DIGEST") || v !== "") out[k] = v;
  for (const key of VERSION_KEYS)
    if (raw[key] === undefined) fail(`versions.env: ${key}:? missing`);
  return out as Versions;
}

export type { LockEntry, Lockfile } from "./lock.ts";
export type ResolvedImages = { kernel: string; node: string; worker: string };

/** The three framework image refs before registry resolution: a registry
 * ref passes through; "local" names the locally-BUILT tag (the `up` driver
 * materialises it; a pushed multi-box bundle needs registry refs — see
 * README). `renderWithRegistry` is what turns a `name:tag` into a digest. */
export function resolveImages(roles: RolesFile): ResolvedImages {
  return {
    kernel: roles.images.kernel === "local" ? LOCAL_KERNEL_IMAGE : roles.images.kernel,
    node: roles.images.node === "local" ? LOCAL_NODE_IMAGE : roles.images.node,
    worker: roles.images.worker === "local" ? LOCAL_WORKER_IMAGE : roles.images.worker,
  };
}

export interface BundleEnv {
  file: string;
}

export interface RenderedBox {
  name: string;
  dir: string;
  composeProject: string;
  kernelBox: boolean;
  env: BundleEnv;
}

export interface RenderResult {
  envName: string;
  outDir: string;
  boxes: RenderedBox[];
  /** URL the browser + CLI ride (host-facing, per topology). */
  kernelUrl: string;
  idpUrl: string;
  issuer: string;
}

function copyTree(src: string, dest: string): void {
  mkdirSync(dest, { recursive: true });
  for (const entry of readdirSync(src, { withFileTypes: true })) {
    const from = path.join(src, entry.name);
    const to = path.join(dest, entry.name);
    if (entry.isDirectory()) copyTree(from, to);
    else copyFileSync(from, to);
  }
}

function sub(text: string, subs: Record<string, string>): string {
  let out = text;
  for (const [key, value] of Object.entries(subs)) out = out.replaceAll(`__${key}__`, value);
  const leftover = /__([A-Z0-9_]+)__/.exec(out);
  if (leftover !== null) fail(`template placeholder __${leftover[1]}__ has no renderer value`);
  return out;
}

function envLines(map: Record<string, string>): string {
  return (
    Object.entries(map)
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([k, v]) => `${k}=${v}`)
      .join("\n") + "\n"
  );
}

/** Host-facing kernel URL for this topology (browsers, CLI, smoke). */
export function hostKernelUrl(roles: RolesFile): string {
  return roles.topology === "sim"
    ? `http://localhost:${String(roles.kernel.ports.kernel)}`
    : `https://${roles.kernel.host}`;
}

export function hostIdpUrl(roles: RolesFile): string {
  return roles.topology === "sim"
    ? `http://localhost:${String(roles.kernel.ports.idp)}`
    : `https://${roles.kernel.host}/id`;
}

/** The OIDC issuer string — byte-matches KC_HOSTNAME-derived issuer (the
 * alias law: validation compares strings, not origins). */
export function issuerOf(roles: RolesFile): string {
  return roles.topology === "sim"
    ? `http://id.localhost:${String(roles.kernel.ports.idp)}/realms/facilitator`
    : `https://${roles.kernel.host}/id/realms/facilitator`;
}

/** Kernel origin as node/worker containers reach it: the shared-network
 * service name in sim; the public hostname over LAN/tailscale elsewhere. */
export function kernelServiceUrl(roles: RolesFile): string {
  return roles.topology === "sim" ? "http://facilitator:8080" : `https://${roles.kernel.host}`;
}

/** OTLP collector as node boxes reach it (plain exporter port, published
 * by the kernel bundle outside sim; service name on the spine in sim). */
export function otlpEndpoint(roles: RolesFile): string {
  return roles.topology === "sim" ? "http://aspire:18890" : `http://${roles.kernel.host}:18890`;
}

/** Box name = node host's first label — filesystem-safe, predictable. */
export function nodeSlug(host: string): string {
  const base = host.split(".")[0] ?? "";
  if (!/^[a-z0-9-]+$/i.test(base)) fail(`node host "${host}":? first label must be [a-z0-9-]+`);
  return base.toLowerCase();
}

/** The kernel's single armed launcher (single-fleet law): the first node
 * declaring launcher: true. */
export function launcherEndpoint(roles: RolesFile): string {
  const armed = roles.nodes.find((n) => n.launcher);
  if (armed === undefined)
    fail("no node has launcher: true — the kernel's provisioning block needs one armed launcher");
  return `launcher.${nodeSlug(armed.host)}`;
}

function childSecretKey(roles: RolesFile, team: string): string {
  const key = `CHILD_SECRETS_${team.toUpperCase().replace(/[^A-Z0-9]/g, "_")}`;
  requireSecret(readDotEnv(path.join(EXAMPLE_DIR, roles.secretsFile)), key, roles.secretsFile);
  return key;
}

/** The ONE kernel env source (reference/config/facilitator.env law): every
 * value literal except the launcher's $$-name-indirects. */
export function kernelFacilitatorEnv(
  roles: RolesFile,
  secrets: Record<string, string>,
  images: ResolvedImages,
): Record<string, string> {
  const team = roles.kernel.teams[0] as string;
  return {
    ConnectionStrings__Facilitator: `Host=postgres;Database=facilitator;Username=home;Password=${requireSecret(secrets, "POSTGRES_PASSWORD", roles.secretsFile)}`,
    Link__EnrollmentToken: requireSecret(secrets, "OFAC_ENROLLMENT_TOKEN", roles.secretsFile),
    Link__HeartbeatSeconds: "5",
    Storage__Provider: "s3",
    Storage__S3__Endpoint: "http://artifacts:8333",
    Storage__S3__Bucket: "facilitator-artifacts",
    Storage__S3__Region: "us-east-1",
    Storage__S3__AccessKey: requireSecret(secrets, "S3_ACCESS_KEY", roles.secretsFile),
    Storage__S3__SecretKey: requireSecret(secrets, "S3_SECRET_KEY", roles.secretsFile),
    Storage__S3__ForcePathStyle: "true",
    Otel__Endpoint: "http://aspire:18890",
    OTEL_EXPORTER_OTLP_ENDPOINT: "http://aspire:18890",
    OTEL_EXPORTER_OTLP_PROTOCOL: "http/protobuf",
    OTEL_SERVICE_NAME: "facilitator",
    Provision__LauncherEndpoint: launcherEndpoint(roles),
    Provision__SweepIntervalSeconds: "10",
    Provision__Workloads__0__WorkloadId: "gw.elastic",
    Provision__Workloads__0__Team: team,
    Provision__Workloads__0__Image: images.worker,
    Provision__Workloads__0__Network: SHARED_NETWORK,
    Provision__Workloads__0__Entrypoint__0: "/bin/sh",
    Provision__Workloads__0__Entrypoint__1: "/opt/ofac/ofac-supervisor.sh",
    Provision__Workloads__0__Env__UNITS_DIR: "/opt/ofac/units",
    Provision__Workloads__0__Env__OFAC_LOG_DIR: "/var/lib/ofac/logs",
    Provision__Workloads__0__Env__OFAC_KERNEL_URL: "http://facilitator:8080",
    Provision__Workloads__0__Env__OFAC_WORKER_TEAM: team,
    Provision__Workloads__0__Env__OFAC_WORKER_ENDPOINT: "elastic-worker-child",
    // $$ = name-indirect: the launcher expands these from ITS env.
    Provision__Workloads__0__Env__OFAC_ENROLL_SECRET: "$$OFAC_ENROLLMENT_TOKEN",
    Provision__Workloads__0__Env__OFAC_CHILD_SECRET: `$$${childSecretKey(roles, team)}`,
    Provision__Workloads__0__Env__OFAC_SUPERVISOR_MAX_RESTARTS: "3",
    "Provision__Workloads__0__Labels__io.facilitator.managed": "kernel-provisioned",
  };
}

/** The rendered identity/gate config: the reference table trimmed to this
 * example's scope + the enrollment-tokens.manage binding (the mint gate is
 * framework-admin only). */
export function facilitatorJson(roles: RolesFile, issuer: string): string {
  const cfg = {
    ui: { oidcAuthority: issuer, oidcClientId: "facilitator-api" },
    Link: {
      Teams: roles.kernel.teams,
      Evidence: { Audience: "facilitator-link" },
    },
    Logging: {
      LogLevel: {
        Default: "Information",
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning",
      },
    },
    identity: {
      oidc: { authority: issuer, audience: "facilitator-api", requireHttps: false },
      claims: {
        subject: "/sub",
        displayName: "/preferred_username",
        groups: [{ path: "/groups", shape: "string-array" }],
        scopes: [
          { path: "/scope", shape: "space-delimited-string" },
          { path: "/scp", shape: "string-array-or-space-delimited" },
        ],
        permissions: [{ path: "/permissions", shape: "string-array" }],
        authenticationMethods: [{ path: "/amr", shape: "string-array" }],
        authenticationContext: "/acr",
      },
      roles: {
        "kernel-operator": [
          { all: [{ claim: "/realm_access/roles", op: "contains", value: "kernel-operator" }] },
        ],
        "framework-admin": [
          { all: [{ claim: "/realm_access/roles", op: "contains", value: "framework-admin" }] },
        ],
        "team-member": [{ all: [{ claim: "/groups", op: "exists" }] }],
      },
      authorization: {
        operations: [
          {
            id: "work.write",
            methods: ["POST"],
            paths: [
              "/api/works",
              "/api/works/{id}/steer",
              "/api/works/{id}/transitions",
              "/api/ingress/events",
            ],
            rolesAnyOf: ["kernel-operator"],
          },
          {
            id: "artifacts.write",
            methods: ["POST", "PUT"],
            paths: [
              "/api/artifacts/request-upload",
              "/api/artifacts/{id}/content",
              "/api/artifacts/{id}/confirm",
            ],
            rolesAnyOf: ["kernel-operator", "framework-admin"],
          },
          {
            id: "admin.read",
            methods: ["GET"],
            paths: [
              "/api/admin/summary",
              "/api/admin/search",
              "/api/admin/projects/{id}",
              "/api/admin/work/{id}",
              "/api/admin/work/{id}/timeline",
              "/api/admin/links",
              "/api/admin/deliveries",
              "/api/admin/provisioning",
              "/api/admin/metrics",
              "/api/admin/config",
              "/api/admin/config/{resource}",
              "/api/admin/config/{resource}/history",
            ],
            rolesAnyOf: ["kernel-operator", "framework-admin"],
          },
          {
            id: "admin.mutate",
            methods: ["POST", "PATCH"],
            paths: [
              "/api/admin/dead-letters/{deadLetterId}/replay",
              "/api/admin/manifests/{manifestId}/re-mark",
              "/api/admin/config/{resource}",
            ],
            rolesAnyOf: ["kernel-operator", "framework-admin"],
          },
          {
            id: "enrollment-tokens.manage",
            methods: ["POST", "GET", "DELETE"],
            paths: ["/api/admin/enroll-tokens", "/api/admin/enroll-tokens/{id}"],
            rolesAnyOf: ["framework-admin"],
          },
        ],
      },
    },
  };
  return `${JSON.stringify(cfg, null, 2)}\n`;
}

function realmJson(roles: RolesFile): string {
  const template = readFileSync(path.join(EXAMPLE_DIR, "keycloak", "realm.json"), "utf8");
  const cliSecret = requireSecret(
    readDotEnv(path.join(EXAMPLE_DIR, roles.secretsFile)),
    "IDP_CLI_SECRET",
    roles.secretsFile,
  );
  const redirects = [
    `http://localhost:${String(roles.kernel.ports.kernel)}/*`,
    `http://id.localhost:${String(roles.kernel.ports.idp)}/*`,
    ...(roles.topology === "sim" ? [] : [`https://${roles.kernel.host}/*`]),
  ]
    .map((u) => `"${u}"`)
    .join(", ");
  const groups = roles.kernel.teams.map((t) => `"/teams/${t}"`).join(", ");
  const subGroups = roles.kernel.teams
    .map((t) => `{"name": "${t}", "path": "/teams/${t}", "subGroups": []}`)
    .join(", ");
  return sub(template, {
    API_REDIRECTS: redirects,
    CLI_SECRET: cliSecret,
    TEAM_SUBGROUPS: subGroups,
    ADMIN_GROUPS: groups,
  });
}

/** Compose `image:` line (+ build block when the ref is a locally-built
 * tag in sim, where the repo checkout is right here). A registry ref
 * renders `image:` and NOTHING else — the digest law. */
function imageBlock(ref: string, kind: "kernel" | "node", sim: boolean): string {
  const localName = kind === "kernel" ? LOCAL_KERNEL_IMAGE : LOCAL_NODE_IMAGE;
  if (!sim || ref !== localName) return `image: ${ref}`;
  const dockerfile = kind === "kernel" ? "src/facilitator/Dockerfile" : "Dockerfile.node";
  return [
    `image: ${localName}`,
    "    build:",
    `      context: ${REPO_CONTEXT}`,
    `      dockerfile: ${dockerfile}`,
  ].join("\n");
}

// Third-party images: the digest pin wins when set (reference's
// `:${TAG}@${DIGEST:+…}` law, computed at render so the bundle is exact).
function thirdPartyImage(repo: string, versions: Versions, key: string): string {
  const digest = versions[`${key}_DIGEST`];
  return digest === undefined || digest === ""
    ? `${repo}:${versions[`${key}_TAG`] as string}`
    : `${repo}@${digest}`;
}

function caddyBlock(roles: RolesFile, versions: Versions): { block: string; volumes: string } {
  if (roles.topology === "sim") return { block: "", volumes: "" };
  const services = [
    "  # Caddy fronts the single 443 site (tls internal / acme) and terminates",
    "  # TLS toward the container network. The tailscale sidecar (tailscale",
    "  # topology) rides the host stack and steers 443 in.",
    "  caddy:",
    `    image: ${thirdPartyImage("caddy", versions, "CADDY")}`,
    "    ports:",
    '      - "443:443"',
    "    volumes:",
    "      - ./Caddyfile:/etc/caddy/Caddyfile:ro",
    "      - caddy_data:/data",
    "      - caddy_config:/config",
    "    depends_on:",
    "      - facilitator",
    "      - keycloak",
    "    restart: on-failure:5",
  ];
  const vols = ["caddy_data: {}", "caddy_config: {}"];
  if (roles.topology === "tailscale" && roles.tailscale !== undefined) {
    services.push(
      "  tailscale:",
      `    image: ${thirdPartyImage("tailscale/tailscale", versions, "TAILSCALE")}`,
      "    hostname: ofac-kernel",
      "    environment:",
      "      TS_AUTHKEY: ${" +
        roles.tailscale.keyEnv +
        ":?tailscale authkey must be in the operator environment}",
      ...(roles.tailscale.funnel ? ['      TS_ENABLE_FUNNEL: "true"'] : []),
      // Host stack: tailscale takes over the box's own 443 and hands it to
      // Caddy's published port (funnel + MagicDNS both land there).
      "    network_mode: host",
      "    volumes:",
      "      - tailscale_state:/var/lib/tailscale",
      "    devices:",
      "      - /dev/net/tun",
      "    cap_add: [NET_ADMIN]",
      "    restart: on-failure:5",
    );
    vols.push("tailscale_state: {}");
  }
  return { block: `${services.join("\n")}\n`, volumes: vols.join("\n  ") };
}

function renderCaddyfile(roles: RolesFile): string {
  return sub(readTemplate("kernel", "Caddyfile.tmpl"), {
    SITE_HOST: roles.kernel.host,
    TLS_DIRECTIVE: roles.tls === "acme" ? "auto_certs" : "internal",
  });
}

function readTemplate(box: "kernel" | "node", file: string): string {
  return readFileSync(path.join(TEMPLATES, box, file), "utf8");
}

export async function render(
  roles: RolesFile,
  opts: { forceResolve?: boolean } = {},
): Promise<RenderResult> {
  const versions = loadVersions();
  const secretsFile = path.join(EXAMPLE_DIR, roles.secretsFile);
  const secrets = readDotEnv(secretsFile);
  for (const key of [
    "OFAC_ENROLLMENT_TOKEN",
    "POSTGRES_PASSWORD",
    "S3_ACCESS_KEY",
    "S3_SECRET_KEY",
    "IDP_CLI_SECRET",
    "KEYCLOAK_ADMIN_PASSWORD",
  ])
    requireSecret(secrets, key, roles.secretsFile);

  const envName = roles.name;
  const lock = loadLock(EXAMPLE_DIR, envName);
  const images: ResolvedImages = {
    kernel: await resolveImage(
      "kernel",
      roles.images.kernel,
      LOCAL_KERNEL_IMAGE,
      lock,
      opts.forceResolve === true,
    ),
    node: await resolveImage(
      "node",
      roles.images.node,
      LOCAL_NODE_IMAGE,
      lock,
      opts.forceResolve === true,
    ),
    worker: await resolveImage(
      "worker",
      roles.images.worker,
      LOCAL_WORKER_IMAGE,
      lock,
      opts.forceResolve === true,
    ),
  };
  const issuer = issuerOf(roles);
  const outDir = path.join(OUT_ROOT, envName);
  rmSync(outDir, { recursive: true, force: true });
  mkdirSync(outDir, { recursive: true });

  const boxes: RenderedBox[] = [];
  const sim = roles.topology === "sim";
  const ports = roles.kernel.ports;

  // ---- kernel bundle ----------------------------------------------------
  const kernelDir = path.join(outDir, "kernel");
  copyTree(path.join(TEMPLATES, "kernel", "postgres-init"), path.join(kernelDir, "postgres-init"));
  mkdirSync(path.join(kernelDir, "keycloak"), { recursive: true });
  writeFileSync(path.join(kernelDir, "keycloak", "realm.json"), realmJson(roles));
  writeFileSync(path.join(kernelDir, "facilitator.json"), facilitatorJson(roles, issuer));

  const extra = caddyBlock(roles, versions);
  // Non-sim publishes the plain OTLP port for the node boxes (outbound-only
  // toward the kernel); the UI stays container-local, fronted as configured.
  const aspirePorts = sim
    ? `ports:\n      - "127.0.0.1:${String(ports.aspireUi)}:18888"\n      - "127.0.0.1:7091:18890"`
    : 'ports:\n      - "18890:18890"';
  const composeText = sub(readTemplate("kernel", "compose.yaml.tmpl"), {
    IMAGE_POSTGRES: thirdPartyImage("postgres", versions, "POSTGRES"),
    IMAGE_KEYCLOAK: thirdPartyImage("quay.io/keycloak/keycloak", versions, "KEYCLOAK"),
    IMAGE_S3: thirdPartyImage("chrislusf/seaweedfs", versions, "SEAWEEDFS"),
    IMAGE_ASPIRE: thirdPartyImage("mcr.microsoft.com/dotnet/aspire-dashboard", versions, "ASPIRE"),
    KC_HOSTNAME:
      roles.topology === "sim"
        ? `http://id.localhost:${String(ports.idp)}`
        : `https://${roles.kernel.host}/id`,
    // sim: the issuer is http://id.localhost:<idp>/... — Keycloak must
    // LISTEN on <idp> (KC_HTTP_PORT) and answer `id.localhost` INSIDE the
    // spine, or the kernel's JWKS fetch fails and every admin call 401s
    // (reference/'s alias law: issuer byte-exact, container == host
    // reachability). Non-sim: Caddy proxies keycloak:8080 under /id/*.
    KC_HTTP: sim ? `KC_HTTP_PORT: "${String(ports.idp)}"` : "",
    KC_ALIAS: sim ? "networks:\n      default:\n        aliases: [id.localhost]" : "",
    KC_PORTS: sim
      ? `ports:\n      - "127.0.0.1:${String(ports.idp)}:${String(ports.idp)}"`
      : "ports: []",
    ASPIRE_PORTS: aspirePorts,
    KERNEL_IMAGE: imageBlock(images.kernel, "kernel", sim),
    KERNEL_PORTS: sim ? `ports:\n      - "127.0.0.1:${String(ports.kernel)}:8080"` : "ports: []",
    EXTRA_BLOCK: extra.block,
    EXTRA_VOLUMES: extra.volumes,
  });
  writeFileSync(path.join(kernelDir, "compose.yaml"), composeText);
  parseYaml(composeText); // a template slip must never reach docker

  writeFileSync(
    path.join(kernelDir, "facilitator.env"),
    envLines(kernelFacilitatorEnv(roles, secrets, images)),
  );
  const kernelDotEnv: Record<string, string> = {
    ...versions,
    POSTGRES_PASSWORD: secrets["POSTGRES_PASSWORD"] as string,
    S3_ACCESS_KEY: secrets["S3_ACCESS_KEY"] as string,
    S3_SECRET_KEY: secrets["S3_SECRET_KEY"] as string,
    KEYCLOAK_ADMIN_PASSWORD: secrets["KEYCLOAK_ADMIN_PASSWORD"] as string,
    WORKER_IMAGE: images.worker,
    OTLP_ENDPOINT: "http://aspire:18890",
    OFAC_TEAM: roles.kernel.teams[0] as string,
  };
  const kernelDotEnvFile = path.join(kernelDir, ".env");
  writeFileSync(kernelDotEnvFile, envLines(kernelDotEnv));
  if (!sim) writeFileSync(path.join(kernelDir, "Caddyfile"), renderCaddyfile(roles));
  boxes.push({
    name: "kernel",
    dir: kernelDir,
    composeProject: "ofackernel",
    kernelBox: true,
    env: { file: kernelDotEnvFile },
  });

  // ---- node bundles -----------------------------------------------------
  const nodeTemplate = readTemplate("node", "compose.yaml.tmpl");
  for (const node of roles.nodes) {
    const slug = nodeSlug(node.host);
    const dir = path.join(outDir, slug);
    copyTree(path.join(TEMPLATES, "node", "units"), path.join(dir, "units"));
    copyTree(
      path.join(EXAMPLE_DIR, "..", "..", "features", "shared"),
      path.join(dir, "supervisor"),
    );
    mkdirSync(path.join(dir, "launcher"), { recursive: true });
    const team = node.teams[0] as string;
    const subs = {
      NODE: slug,
      GW_ID: node.gatewayId,
      TEAM: team,
      KERNEL_URL: kernelServiceUrl(roles),
      LAUNCHER_ID: `launcher.${slug}`,
      CHILD_SECRETS_ENV: childSecretKey(roles, team),
    };
    for (const [file, template] of [
      ["config/gateway.yaml", "gateway.yaml.tmpl"],
      ["config/worker.yaml", "worker.yaml.tmpl"],
      ["config/capabilities.yaml", "capabilities.yaml"],
      ["config/policy.yaml", "policy.yaml"],
      ["launcher/adapter.yaml", "launcher-adapter.yaml.tmpl"],
    ] as const) {
      mkdirSync(path.dirname(path.join(dir, file)), { recursive: true });
      writeFileSync(path.join(dir, file), sub(readTemplate("node", template), subs));
    }
    const nodeCompose = sub(nodeTemplate, {
      NODE: slug,
      NODE_IMAGE_BLOCK: imageBlock(images.node, "node", sim),
      NODE_IMAGE_BLOCK_2: imageBlock(images.node, "node", sim),
      CHILD_SECRETS_ENV: childSecretKey(roles, team),
    });
    writeFileSync(path.join(dir, "compose.yaml"), nodeCompose);
    parseYaml(nodeCompose);
    const env: Record<string, string> = {
      // enroll-token writes the minted gateway token here; until then the
      // compose `:?` fails closed — up before enroll is a clear error.
      OFAC_ENROLL_SECRET: "pending",
      OFAC_KERNEL_URL: kernelServiceUrl(roles),
      OFAC_ENROLLMENT_TOKEN: secrets["OFAC_ENROLLMENT_TOKEN"] as string,
      OFAC_TEAM: team,
      OFAC_WORKER_ENDPOINT: `worker.${slug}-child`,
      [childSecretKey(roles, team)]: secrets[childSecretKey(roles, team)] as string,
      OTLP_ENDPOINT: otlpEndpoint(roles),
      WORKER_IMAGE: images.worker,
    };
    const dotEnvFile = path.join(dir, ".env");
    writeFileSync(dotEnvFile, envLines(env));
    boxes.push({
      name: slug,
      dir,
      composeProject: `ofacnode-${slug}`,
      kernelBox: false,
      env: { file: dotEnvFile },
    });
  }
  // ---- lock: what this render resolved ----------------------------------
  const stamp = new Date(0).toISOString(); // deterministic: render stamps no clock
  for (const key of ["kernel", "node", "worker"] as const) {
    const ref = images[key];
    const previous = lock.resolved[key];
    lock.resolved[key] = ref;
    if (previous !== ref)
      lock.history.push({
        image: key,
        resolved: ref,
        from: previous ?? "unset",
        timestamp: stamp,
      });
  }
  saveLock(EXAMPLE_DIR, envName, lock);

  return {
    envName,
    outDir,
    boxes,
    kernelUrl: hostKernelUrl(roles),
    idpUrl: hostIdpUrl(roles),
    issuer,
  };
}

/** Rebuild the box layout for a rendered env WITHOUT re-rendering (an up
 * after `enroll-token` must never clobber the written OFAC_ENROLL_SECRET).
 * Throws when the env was never rendered. */
export function loadBundles(roles: RolesFile): RenderedBox[] {
  const outDir = path.join(OUT_ROOT, roles.name);
  if (!existsSync(outDir))
    fail(`${outDir}:? not rendered — run localctl render --env ${roles.name}`);
  const names = ["kernel", ...roles.nodes.map((n) => nodeSlug(n.host))];
  return names.map((name) => {
    const dir = path.join(outDir, name);
    const envFile = path.join(dir, ".env");
    if (!existsSync(path.join(dir, "compose.yaml")))
      fail(`${dir}:? no bundle — run localctl render --env ${roles.name}`);
    return {
      name,
      dir,
      composeProject: name === "kernel" ? "ofackernel" : `ofacnode-${name}`,
      kernelBox: name === "kernel",
      env: { file: envFile },
    };
  });
}
