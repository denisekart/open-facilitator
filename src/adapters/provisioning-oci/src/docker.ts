// The Docker Engine API over a plain unix socket (no SDK): node http.request
// with socketPath, JSON in/out. Only the five container verbs + the pull the
// launcher law needs. A non-2xx answer throws DockerError carrying the status
// and the engine's message, so the handler can turn it into an honest receipt.
import { request } from "node:http";

export class DockerError extends Error {
  constructor(
    readonly status: number,
    readonly methodLabel: string,
    message: string,
  ) {
    super(`${methodLabel}: ${message} (HTTP ${status})`);
    this.name = "DockerError";
  }
}

export interface ContainerSummary {
  id: string;
  state: string;
  /** Engine unix seconds; 0 while running. */
  finishedAt: number;
  labels: Record<string, string>;
}

/** The seam the handler talks to; the test fake implements it verbatim. */
export interface DockerApi {
  /** GET /containers/json?all=1&filters=… — raw label filter terms (bare
   *  `ofac.workload` matches every labeled container; `k=v` narrows). */
  listContainers(labelFilters: readonly string[]): Promise<ContainerSummary[]>;
  /** GET /images/{ref}/json — 200 ⇒ present, 404 ⇒ absent (inspect-first:
   *  a locally built image is never pulled). */
  imageExists(image: string, signal?: AbortSignal): Promise<boolean>;
  /** POST /images/create?fromImage=… (bounded by the caller's signal). */
  pullImage(image: string, signal: AbortSignal): Promise<void>;
  /** POST /containers/create — returns the new id. */
  createContainer(name: string, body: Record<string, unknown>): Promise<string>;
  startContainer(id: string): Promise<void>;
  /** 304 (already stopped) and 404 (gone) are success. */
  stopContainer(id: string, graceSeconds: number): Promise<void>;
  /** 404 (already gone) is success. */
  removeContainer(id: string): Promise<void>;
}

export interface DockerClientOptions {
  socketPath: string;
}

/** `unix:///var/run/docker.sock` → `/var/run/docker.sock`; anything else fails
 *  closed (the launcher only speaks a local socket). */
export function dockerSocketPath(dockerHost: string): string {
  const match = /^unix:\/\/(\/.+)$/.exec(dockerHost);
  if (match === null) {
    throw new Error(`DOCKER_HOST '${dockerHost}' is not a unix:// socket path`);
  }

  return match[1] as string;
}

export class DockerClient implements DockerApi {
  constructor(private readonly opts: DockerClientOptions) {}

  private call(
    method: "GET" | "POST" | "DELETE",
    path: string,
    opts: { body?: unknown; acceptStatuses: number[]; label: string; signal?: AbortSignal },
  ): Promise<unknown> {
    const { promise, resolve, reject } = Promise.withResolvers<unknown>();
    const req = request(
      {
        socketPath: this.opts.socketPath,
        method,
        path,
        headers: { "content-type": "application/json" },
        ...(opts.signal === undefined ? {} : { signal: opts.signal }),
      },
      (res) => {
        const chunks: Buffer[] = [];
        res.on("data", (c: Buffer) => chunks.push(c));
        res.on("end", () => {
          const status = res.statusCode ?? 0;
          const text = Buffer.concat(chunks).toString("utf8");
          if (!opts.acceptStatuses.includes(status)) {
            let detail = text.slice(0, 512);
            try {
              const parsed = JSON.parse(text) as { message?: string };
              if (typeof parsed.message === "string") detail = parsed.message;
            } catch {
              /* raw body is the detail */
            }

            reject(new DockerError(status, opts.label, detail));
            return;
          }

          if (text.length === 0) resolve(undefined);
          else resolve(JSON.parse(text));
        });
      },
    );
    req.on("error", reject);
    if (opts.body !== undefined) req.write(JSON.stringify(opts.body));
    req.end();
    return promise;
  }

  async listContainers(labelFilters: readonly string[]): Promise<ContainerSummary[]> {
    const filters = JSON.stringify({ label: [...labelFilters] });
    const body = await this.call(
      "GET",
      `/containers/json?all=1&filters=${encodeURIComponent(filters)}`,
      {
        acceptStatuses: [200],
        label: "list",
      },
    );
    const rows = Array.isArray(body) ? body : [];
    return rows.map((row) => {
      const r = row as {
        Id: string;
        State: string;
        FinishedAt: number;
        Labels?: Record<string, string> | null;
      };
      return {
        id: r.Id,
        state: r.State,
        finishedAt: r.FinishedAt,
        labels: r.Labels ?? {},
      };
    });
  }

  async imageExists(image: string, signal?: AbortSignal): Promise<boolean> {
    // The Engine routes image names as multi-segment paths (library/x:tag),
    // so the ref rides the URL raw. 404 is an answer, not an error: absent.
    try {
      await this.call("GET", `/images/${image}/json`, {
        acceptStatuses: [200],
        label: `inspect ${image}`,
        ...(signal === undefined ? {} : { signal }),
      });
      return true;
    } catch (error) {
      if (error instanceof DockerError && error.status === 404) return false;
      throw error;
    }
  }

  async pullImage(image: string, signal: AbortSignal): Promise<void> {
    // The engine streams progress JSON until the pull completes; the promise
    // resolves at EOF. The caller's signal enforces the command deadline.
    await this.call("POST", `/images/create?fromImage=${encodeURIComponent(image)}`, {
      acceptStatuses: [200, 201],
      label: `pull ${image}`,
      signal,
    });
  }

  async createContainer(name: string, body: Record<string, unknown>): Promise<string> {
    const parsed = (await this.call("POST", `/containers/create?name=${encodeURIComponent(name)}`, {
      body,
      acceptStatuses: [201],
      label: `create ${name}`,
    })) as { Id?: string };
    if (typeof parsed?.Id !== "string") {
      throw new DockerError(500, `create ${name}`, "engine answered without an Id");
    }

    return parsed.Id;
  }

  async startContainer(id: string): Promise<void> {
    await this.call("POST", `/containers/${id}/start`, {
      acceptStatuses: [204, 304],
      label: `start ${id}`,
    });
  }

  async stopContainer(id: string, graceSeconds: number): Promise<void> {
    await this.call("POST", `/containers/${id}/stop?t=${graceSeconds}`, {
      acceptStatuses: [204, 304, 404],
      label: `stop ${id}`,
    });
  }

  async removeContainer(id: string): Promise<void> {
    await this.call("DELETE", `/containers/${id}`, {
      acceptStatuses: [204, 404],
      label: `rm ${id}`,
    });
  }
}
