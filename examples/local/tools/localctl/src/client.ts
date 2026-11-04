// Kernel + Keycloak client for the mint/list/revoke + smoke legs. The
// kernel API is the shared contract: POST/GET /api/admin/enroll-tokens,
// DELETE /api/admin/enroll-tokens/{id}; CLI auth rides the example realm's
// facilitator-cli client_credentials (the same trick as referencectl token).
// Responses are external data: every read narrows the fields it consumes.
import { http, fail } from "./util.js";

export interface MintedToken {
  id: string;
  token: string;
  expiresAt: string;
}

export interface TokenRow {
  id: string;
  label: string;
  team: string;
  role: string;
  expiresAt: string;
  consumedAt: string | null;
  consumedEndpointId: string | null;
  revokedAt: string | null;
}

export interface LinkRow {
  endpointId: string;
  team: string;
  online: boolean;
}

function obj(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    fail(`kernel response is not a JSON object: ${JSON.stringify(value)?.slice(0, 240)}`);
  return value as Record<string, unknown>;
}

function strField(value: Record<string, unknown>, key: string): string {
  const v = value[key];
  if (typeof v !== "string") fail(`kernel response field ${key}:? not a string`);
  return v;
}

function arrField(value: Record<string, unknown>, key: string): unknown[] {
  const v = value[key];
  if (!Array.isArray(v)) fail(`kernel response field ${key}:? not an array`);
  return v;
}
type Json = Record<string, unknown> | unknown[];

async function getRaw(url: string, token: string): Promise<Json> {
  const r = await http("GET", url, { token });
  if (r.status !== 200) fail(`GET ${url} -> ${r.status}: ${r.body.slice(0, 240)}`);
  const parsed: unknown = JSON.parse(r.body);
  if (Array.isArray(parsed)) return parsed;
  return obj(parsed);
}

/** A Json view's list under `key`, or the view itself when it is already
 * the bare array (routes differ in envelope posture; both are accepted). */
function listField(view: Json, key: string): unknown[] {
  return Array.isArray(view) ? view : arrField(view, key);
}

export class KernelClient {
  constructor(
    readonly baseUrl: string,
    readonly token: string,
  ) {}

  async mintToken(body: {
    label: string;
    team: string;
    role: "gateway" | "child";
    ttlMinutes?: number;
  }): Promise<MintedToken> {
    const r = await http("POST", `${this.baseUrl}/api/admin/enroll-tokens`, {
      token: this.token,
      json: body,
    });
    if (r.status !== 201) fail(`mint enroll-token -> ${r.status}: ${r.body.slice(0, 240)}`);
    const j = obj(JSON.parse(r.body) as unknown);
    return { id: strField(j, "id"), token: strField(j, "token"), expiresAt: strField(j, "expiresAt") };
  }

  async listTokens(): Promise<TokenRow[]> {
    const r = await http("GET", `${this.baseUrl}/api/admin/enroll-tokens`, { token: this.token });
    if (r.status !== 200) fail(`list enroll-tokens -> ${r.status}: ${r.body.slice(0, 240)}`);
    const rows = JSON.parse(r.body) as unknown; // BARE array per the contract.
    if (!Array.isArray(rows)) fail("list enroll-tokens: expected a bare JSON array");
    return rows.map((row) => {
      const j = obj(row);
      const nullable = (key: string): string | null =>
        typeof j[key] === "string" ? (j[key] as string) : null;
      return {
        id: strField(j, "id"),
        label: strField(j, "label"),
        team: strField(j, "team"),
        role: strField(j, "role"),
        expiresAt: strField(j, "expiresAt"),
        consumedAt: nullable("consumedAt"),
        consumedEndpointId: nullable("consumedEndpointId"),
        revokedAt: nullable("revokedAt"),
      };
    });
  }

  async revokeToken(id: string): Promise<void> {
    const r = await http("DELETE", `${this.baseUrl}/api/admin/enroll-tokens/${id}`, {
      token: this.token,
    });
    if (r.status !== 204) fail(`revoke enroll-token ${id} -> ${r.status}: ${r.body.slice(0, 240)}`);
  }

  async links(): Promise<LinkRow[]> {
    const view = await getRaw(`${this.baseUrl}/api/admin/links`, this.token);
    return listField(view, "gateways").map((row) => {
      const j = obj(row);
      if (typeof j["online"] !== "boolean") fail("links row carries no boolean `online`");
      return { endpointId: strField(j, "endpointId"), team: strField(j, "team"), online: j["online"] };
    });
  }

  health(): Promise<boolean> {
    return http("GET", `${this.baseUrl}/health`, { timeoutMs: 5_000 }).then(
      (r) => r.status === 200,
      () => false,
    );
  }

  async createWork(body: Record<string, unknown>): Promise<string> {
    const r = await http("POST", `${this.baseUrl}/api/works`, { token: this.token, json: body });
    if (r.status !== 201 && r.status !== 200)
      fail(`POST /api/works -> ${r.status}: ${r.body.slice(0, 240)}`);
    return strField(obj(JSON.parse(r.body) as unknown), "workId");
  }

  async timeline(workId: string): Promise<string[]> {
    const rows = await getRaw(`${this.baseUrl}/api/works/${workId}/timeline`, this.token);
    // Timeline rows arrive as an array under the route's wire name; accept
    // the bare-array form too (the kernel serializes the projection whole).
    const list = Array.isArray(rows) ? rows : arrField(rows, "items");
    return list.map((row) => strField(obj(row), "kind"));
  }

  async manifestCount(workId: string): Promise<number> {
    const rows = await getRaw(`${this.baseUrl}/api/works/${workId}/manifests`, this.token);
    const list = Array.isArray(rows) ? rows : arrField(rows, "items");
    return list.length;
  }

  async patchWorkloadScale(min: number, max: number, idleMinutes: number): Promise<void> {
    const r = await http("PATCH", `${this.baseUrl}/api/admin/config/workload-scale`, {
      token: this.token,
      json: { minWorkers: min, maxWorkers: max, idleShutdownMinutes: idleMinutes },
      headers: { "if-match": "*", "x-change-reason": "localctl-smoke" },
    });
    if (r.status !== 200 && r.status !== 204)
      fail(`PATCH workload-scale -> ${r.status}: ${r.body.slice(0, 240)}`);
  }

  async provisioningStates(): Promise<{ workloadId: string; state: string }[]> {
    const r = await http("GET", `${this.baseUrl}/api/admin/provisioning`, { token: this.token });
    if (r.status !== 200) fail(`GET provisioning -> ${r.status}: ${r.body.slice(0, 240)}`);
    const rows = JSON.parse(r.body) as unknown;
    const list = Array.isArray(rows) ? rows : arrField(obj(rows), "items");
    return list.map((row) => {
      const j = obj(row);
      return { workloadId: strField(j, "workloadId"), state: strField(j, "state") };
    });
  }

  async deadLetterCount(): Promise<number> {
    const body = await getRaw(`${this.baseUrl}/api/admin/deliveries?state=dead_letter`, this.token);
    return arrField(obj(body), "items").length;
  }
}

/** client_credentials against the example realm (issuer rides `idpBaseUrl`). */
export async function mintCliToken(
  idpBaseUrl: string,
  cliSecret: string,
  clientId = "facilitator-cli",
): Promise<string> {
  const response = await fetch(
    `${idpBaseUrl}/realms/facilitator/protocol/openid-connect/token`,
    {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        grant_type: "client_credentials",
        client_id: clientId,
        client_secret: cliSecret,
      }),
      signal: AbortSignal.timeout(10_000),
    },
  );
  const body = obj(await response.json().catch(() => ({})));
  if (response.status !== 200 || typeof body["access_token"] !== "string") {
    const err = typeof body["error"] === "string" ? body["error"] : "no access_token";
    fail(`IdP client_credentials -> ${response.status}: ${err}`);
  }
  return body["access_token"];
}
