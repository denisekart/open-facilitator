// The fake Docker Engine: a real node http.Server listening on a unix socket
// in os.tmpdir(), so the production DockerClient (http.request + socketPath)
// is exercised over the same transport it uses in the field. The server keeps
// a container inventory (seeded per test), applies honest verbs (create→201,
// start/stop/remove→204, 404 on unknown ids), records every request, and can
// stall the pull to pin the deadline law.
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DockerClient } from "../src/docker.js";

export interface FakeContainer {
  id: string;
  state: string; // created | running | exited | dead
  finishedAt: number;
  labels: Record<string, string>;
}

export interface RecordedRequest {
  method: string;
  path: string;
  body?: unknown;
}

export class FakeDocker {
  readonly dir = mkdtempSync(join(tmpdir(), "ofac-oci-fake-"));
  readonly socketPath = join(this.dir, "docker.sock");
  readonly client: DockerClient;
  requests: RecordedRequest[] = [];
  inventory: FakeContainer[] = [];
  /** Locally present image refs (inspect-first law: present ⇒ ZERO pulls). */
  images: string[] = [];
  /** When true POST /images/create answers 404 (unpushed image). */
  pullFails = false;
  /** ms the fake stalls before answering a pull (deadline law). */
  pullDelayMs = 0;
  private readonly server: Server;
  private nextId = 1;

  constructor() {
    this.client = new DockerClient({ socketPath: this.socketPath });
    this.server = createServer((req, res) => void this.handle(req, res));
  }

  async start(): Promise<void> {
    await new Promise<void>((resolve) => this.server.listen(this.socketPath, resolve));
  }

  async stop(): Promise<void> {
    await new Promise<void>((resolve) => this.server.close(() => resolve()));
    rmSync(this.dir, { recursive: true, force: true });
  }

  seed(container: FakeContainer): void {
    this.inventory.push(container);
  }

  /** Requests after the marker index `since` (compact asserts). */
  calls(since = 0): RecordedRequest[] {
    return this.requests.slice(since);
  }

  private async handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(chunk);
    const url = new URL(req.url ?? "/", "http://localhost");
    const record: RecordedRequest = {
      method: req.method ?? "GET",
      path: req.url ?? "",
      ...(chunks.length > 0
        ? { body: JSON.parse(Buffer.concat(chunks).toString("utf8")) as unknown }
        : {}),
    };
    this.requests.push(record);

    const answer = (status: number, body?: unknown): void => {
      res.writeHead(status, { "content-type": "application/json" });
      res.end(body === undefined ? "" : JSON.stringify(body));
    };

    if (req.method === "GET" && url.pathname === "/containers/json") {
      const filters = JSON.parse(url.searchParams.get("filters") ?? "{}") as {
        label?: string[];
      };
      const wanted = filters.label ?? [];
      const rows = this.inventory.filter((c) =>
        wanted.every((term) =>
          term.includes("=")
            ? c.labels[term.slice(0, term.indexOf("="))] === term.slice(term.indexOf("=") + 1)
            : term in c.labels,
        ),
      );
      answer(
        200,
        rows.map((c) => ({
          Id: c.id,
          State: c.state,
          FinishedAt: c.finishedAt,
          Labels: c.labels,
        })),
      );
      return;
    }

    const inspect = /^\/images\/(.+)\/json$/.exec(url.pathname);
    if (req.method === "GET" && inspect !== null) {
      const ref = inspect[1] as string;
      if (this.images.includes(ref)) {
        answer(200, { Id: `img-${ref}` });
      } else {
        answer(404, { message: `No such image: ${ref}` });
      }

      return;
    }

    if (req.method === "POST" && url.pathname === "/images/create") {
      if (this.pullDelayMs > 0) {
        const { promise, resolve } = Promise.withResolvers<void>();
        setTimeout(resolve, this.pullDelayMs);
        await promise;
      }

      const from = url.searchParams.get("fromImage") ?? "";
      if (this.pullFails) {
        answer(404, { message: `pull access denied for ${from}` });
        return;
      }

      this.images.push(from);
      answer(200, {});
      return;
    }

    if (req.method === "POST" && url.pathname === "/containers/create") {
      const body = record.body as { Labels?: Record<string, string> };
      const id = `fake-${this.nextId++}`;
      this.inventory.push({ id, state: "running", finishedAt: 0, labels: body.Labels ?? {} });
      answer(201, { Id: id });
      return;
    }

    const verb = /^\/containers\/([^/]+)\/(start|stop)$/.exec(url.pathname);
    if (req.method === "POST" && verb !== null) {
      const container = this.inventory.find((c) => c.id === verb[1]);
      if (container === undefined) {
        answer(404, { message: `No such container: ${verb[1]}` });
        return;
      }

      container.state = verb[2] === "start" ? "running" : "exited";
      answer(204);
      return;
    }

    if (req.method === "DELETE" && /^\/containers\/[^/]+$/.test(url.pathname)) {
      const id = url.pathname.split("/")[2] as string;
      const index = this.inventory.findIndex((c) => c.id === id);
      if (index === -1) {
        answer(404, { message: `No such container: ${id}` });
        return;
      }

      this.inventory.splice(index, 1);
      answer(204);
      return;
    }

    answer(400, { message: `fake docker: unhandled ${req.method} ${url.pathname}` });
  }
}
