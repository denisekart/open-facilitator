import type { UiConfig } from "./types.js";

/** Kernel ProblemDetails mapped to a typed error (code = the errors.openfacilitator.org tail). */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly title: string;
  readonly detail: string | null;

  constructor(status: number, code: string, title: string, detail: string | null) {
    super(title);
    this.status = status;
    this.code = code;
    this.title = title;
    this.detail = detail;
  }
}

/** Parse a ProblemDetails body; fall back to a synthetic code for non-JSON failures. */
export async function toApiError(response: Response): Promise<ApiError> {
  let code = "unknown";
  let title = `request failed (${response.status})`;
  let detail: string | null = null;
  try {
    const body = (await response.json()) as {
      status?: number;
      title?: string;
      detail?: string;
      type?: string;
    };
    if (typeof body.title === "string") {
      title = body.title;
    }
    if (typeof body.detail === "string") {
      detail = body.detail;
    }
    // type = https://errors.openfacilitator.org/v1/<code>
    if (typeof body.type === "string") {
      const tail = body.type.split("/").pop();
      if (tail) {
        code = tail;
      }
    }
  } catch {
    /* non-JSON error body keeps the synthetic fields */
  }
  return new ApiError(response.status, code, title, detail);
}

export type Fetcher = (path: string, init?: RequestInit) => Promise<Response>;

/** Bearer-token fetch with ONE silent-renew retry on 401 (plan 21 decision 2). */
export function makeApiFetch(
  getToken: () => Promise<string | null>,
  renew: () => Promise<string | null>,
): Fetcher {
  return async (path, init) => {
    const send = async (token: string | null): Promise<Response> => {
      const headers = new Headers(init?.headers);
      if (token) {
        headers.set("Authorization", `Bearer ${token}`);
      }
      if (!headers.has("Accept")) {
        headers.set("Accept", "application/json");
      }
      return fetch(path, { ...init, headers });
    };
    let response = await send(await getToken());
    if (response.status === 401) {
      const fresh = await renew();
      response = await send(fresh);
    }
    return response;
  };
}

/** GET + parse, throwing ApiError on any non-2xx. */
export async function getJson<T>(fetcher: Fetcher, path: string): Promise<T> {
  const response = await fetcher(path);
  if (!response.ok) {
    throw await toApiError(response);
  }
  return (await response.json()) as T;
}

export { uiBootstrap };
let uiBootstrapPromise: Promise<UiConfig> | null = null;
/** Anonymous SPA bootstrap: OIDC authority + client id + dashboard link. */
function uiBootstrap(): Promise<UiConfig> {
  uiBootstrapPromise ??= fetch("/ui-config")
    .then((r) => {
      if (!r.ok) {
        throw new Error(`/ui-config responded ${r.status}`);
      }
      return r.json() as Promise<UiConfig>;
    })
    .catch((err: unknown) => {
      uiBootstrapPromise = null;
      throw err;
    });
  return uiBootstrapPromise;
}
