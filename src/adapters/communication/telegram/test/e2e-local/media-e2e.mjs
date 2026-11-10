// E2E (plan Step 8's verification leg 4): one inbound Telegram photo becomes
// a kernel artifact whose bytes come back through the link-plane content GET,
// and the published mention event carries the ref + the update_id as the
// delivery id.
//
// RUN: `pnpm build && pnpm test:e2e` (this harness imports the COMPILED
// ../../dist/index.js — build first, mirroring the github-connector
// convention of a bare node --test mjs harness, no tsx).
//
// Fakes (no live Telegram, no live kernel):
//   fake Telegram server — the Bot API subset TelegramClient speaks:
//     getMe, deleteWebhook, getUpdates (exactly ONE update then idle),
//     getFile, the /file/bot<token>/… download route (a fixed 1×1 PNG),
//     sendMessage/sendPhoto/setMessageReaction.
//   fake kernel — the link-plane surfaces runProviderAdapter needs, mimicking
//     @ofac/adapter/testing's startStubKernel (which has no enroll route and
//     no hub, hence this): POST /api/link/v1/enroll ⇒ 201 + linkToken,
//     POST /api/link/v1/capabilities ⇒ 204, POST /api/link/v1/events ⇒ 202 +
//     the request_upload ticket in-band on `data`, PUT/GET
//     /api/link/v1/artifacts/<id>/content, and GET back the stored bytes.
//     Plus the SignalR hub the transport dials (WS upgrade on /hubs/link):
//     a minimal JSON-hub-protocol server (handshake `{}`, Hello ⇒ Welcome,
//     Envelope ⇒ EnvelopeAck) — written inline because the package takes no
//     ws dependency.
import { createServer } from "node:http";
import { createHash, randomUUID } from "node:crypto";
import { Buffer } from "node:buffer";
import { connect as createNetConnect } from "node:net";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import assert from "node:assert/strict";
const BOT_TOKEN = "777001:e2e-fake-token";
const GROUP = "-1001234567890";
const HUMAN_ID = 555001;
const UPDATE_ID = 9001;
const MESSAGE_ID = 4243;

// The fixed 1×1 PNG every leg must agree on.
const PNG = Uint8Array.from(
  Buffer.from(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
    "base64",
  ),
);
const PNG_SHA = "sha256:" + createHash("sha256").update(PNG).digest("hex");

// ── the fake Telegram server ───────────────────────────────────────────────

function startTelegram() {
  const calls = { getMe: 0, deleteWebhook: 0, getUpdates: 0, getFile: 0, downloads: 0 };
  // The one update: an admitted chat, text `@bot look`, photo (3 variants —
  // the largest by file_size matches the PNG), a file_unique_id.
  const update = {
    update_id: UPDATE_ID,
    message: {
      message_id: MESSAGE_ID,
      chat: { id: Number(GROUP), type: "supergroup", title: "OFAC Lab" },
      from: { id: HUMAN_ID, is_bot: false, first_name: "Human" },
      date: 1730000000,
      text: "@bot look",
      photo: [
        { file_id: "smallA", file_unique_id: "uniqE2E", file_size: 100 },
        { file_id: "tinyB", file_unique_id: "uniqE2E", file_size: 50 },
        { file_id: "largeC", file_unique_id: "uniqE2E", file_size: PNG.byteLength },
      ],
    },
  };
  let delivered = false;
  const server = createServer((request, response) => {
    const chunks = [];
    request.on("data", (c) => chunks.push(c));
    request.on("end", () => {
      const url = request.url ?? "";
      const method = url.split("/").pop() ?? "";
      const answer = (result) => {
        response.writeHead(200, { "content-type": "application/json" });
        response.end(JSON.stringify({ ok: true, result }));
      };
      if (url.startsWith("/file/bot")) {
        calls.downloads += 1;
        response.writeHead(200, { "content-type": "image/png" });
        response.end(Buffer.from(PNG));
        return;
      }
      switch (method) {
        case "getMe":
          calls.getMe += 1;
          answer({ id: 777001, is_bot: true, username: "bot", first_name: "OFAC" });
          return;
        case "deleteWebhook":
          calls.deleteWebhook += 1;
          answer(true);
          return;
        case "getUpdates": {
          calls.getUpdates += 1;
          if (delivered) {
            // Idle: an empty long-poll batch (the loop keeps polling).
            answer([]);
            return;
          }
          delivered = true;
          answer([update]);
          return;
        }
        case "getFile":
          calls.getFile += 1;
          answer({
            file_id: "largeC",
            file_unique_id: "uniqE2E",
            file_size: PNG.byteLength,
            file_path: "photo/file_0.jpg",
          });
          return;
        case "sendMessage":
        case "sendPhoto":
        case "setMessageReaction":
          answer({ message_id: 6001, chat: { id: Number(GROUP), type: "supergroup" } });
          return;
        default:
          response.writeHead(404);
          response.end(JSON.stringify({ ok: false, error_code: 404 }));
      }
    });
  });
  return new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      resolve({
        url: `http://127.0.0.1:${address.port}`,
        calls,
        close: () => new Promise((r) => server.close(() => r())),
      });
    });
  });
}

// ── the minimal WebSocket server (no `ws` dependency) ──────────────────────

const GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

function encodeFrame(text) {
  const payload = Buffer.from(text, "utf8");
  const header =
    payload.length < 126
      ? [0x81, payload.length]
      : payload.length < 65536
        ? [0x81, 126, payload.length >> 8, payload.length & 0xff]
        : [0x81, 127, 0, 0, 0, 0, 0, 0, 0, payload.length];
  return Buffer.concat([Buffer.from(header), payload]);
}

/** Accumulate a socket stream into complete (opcode, payload) frames. */
function frameReader(onFrame) {
  let buffer = Buffer.alloc(0);
  return (chunk) => {
    buffer = Buffer.concat([buffer, chunk]);
    for (;;) {
      if (buffer.length < 2) return;
      const opcode = buffer[0] & 0x0f;
      const masked = (buffer[1] & 0x80) === 0x80;
      let length = buffer[1] & 0x7f;
      let offset = 2;
      if (length === 126) {
        if (buffer.length < offset + 2) return;
        length = buffer.readUInt16BE(offset);
        offset += 2;
      } else if (length === 127) {
        if (buffer.length < offset + 8) return;
        length = buffer.readUIntBE(offset, 8);
        offset += 8;
      }
      let mask = Buffer.alloc(0);
      if (masked) {
        if (buffer.length < offset + 4) return;
        mask = buffer.subarray(offset, offset + 4);
        offset += 4;
      }
      if (buffer.length < offset + length) return;
      const payload = Buffer.from(buffer.subarray(offset, offset + length));
      if (masked) for (let i = 0; i < payload.length; i += 1) payload[i] ^= mask[i % 4];
      buffer = buffer.subarray(offset + length);
      onFrame(opcode, payload);
    }
  };
}

// ── the fake kernel: link-plane REST + the SignalR hub ─────────────────────

function startKernel() {
  const artifacts = new Map(); // id → {bytes, mediaType, digest}
  const pending = new Map(); // commandId → id
  const events = []; // every envelope the fake kernel received (REST or hub)
  const sockets = new Set();

  const server = createServer((request, response) => {
    const chunks = [];
    request.on("data", (c) => chunks.push(c));
    request.on("end", () => {
      const body = Buffer.concat(chunks);
      const url = request.url ?? "";
      const send = (status, obj) => {
        response.writeHead(status, { "content-type": "application/json" });
        response.end(JSON.stringify(obj));
      };
      const put = /^\/api\/link\/v1\/artifacts\/([0-9a-f-]+)\/content$/.exec(url);
      if (request.method === "POST" && url === "/api/link/v1/enroll") {
        send(201, {
          enrollmentId: "e2e-enrollment",
          trustMode: "isolated-executor",
          linkToken: "lnk-e2e",
        });
        return;
      }
      if (request.method === "POST" && url === "/api/link/v1/capabilities") {
        response.writeHead(204);
        response.end();
        return;
      }
      if (request.method === "POST" && url === "/api/link/v1/events") {
        handleEventEnvelope(JSON.parse(body.toString("utf8")), send);
        return;
      }
      if (request.method === "PUT" && put !== null) {
        const id = put[1];
        const row = artifacts.get(id);
        if (row === undefined) {
          send(404, { status: 404, detail: "no such allocation" });
          return;
        }
        const digest = "sha256:" + createHash("sha256").update(body).digest("hex");
        if (digest !== row.digest) {
          send(422, { status: 422, detail: `digest mismatch: ${digest}` });
          return;
        }
        artifacts.set(id, { bytes: body, mediaType: row.mediaType, digest });
        send(200, { artifactId: id, digest, size: body.byteLength, state: "stored" });
        return;
      }
      if (request.method === "GET" && put !== null) {
        const row = artifacts.get(put[1]);
        if (row === undefined || row.bytes.length === 0) {
          send(404, { status: 404, detail: "no such artifact" });
          return;
        }
        response.writeHead(200, { "content-type": row.mediaType });
        response.end(row.bytes);
        return;
      }
      send(404, { status: 404, detail: `fake kernel: ${request.method} ${url}` });
    });
  });

  /** The request_upload/confirm + generic 202 legs (testing.ts behavior). */
  function handleEventEnvelope(envelope, send) {
    const data = envelope.data ?? {};
    events.push(envelope);
    if (envelope.type === "org.facilitator.command.artifact.request_upload.v1") {
      const commandId = String(data.commandId ?? "");
      const existing = pending.get(commandId);
      const artifactId = existing ?? randomUUID().replaceAll("-", "");
      pending.set(commandId, artifactId);
      artifacts.set(artifactId, {
        bytes: Buffer.alloc(0),
        mediaType: String(data.mediaType ?? "application/octet-stream"),
        digest: String(data.digest ?? ""),
      });
      send(202, {
        deliveryId: randomUUID(),
        status: existing === undefined ? "accepted" : "duplicate",
        processedAt: new Date().toISOString(),
        data: {
          artifactId,
          uploadLocation: `/api/link/v1/artifacts/${artifactId}/content`,
          method: "POST",
          expiresAt: new Date(Date.now() + 900_000).toISOString(),
        },
      });
      return;
    }
    send(202, {
      deliveryId: randomUUID(),
      status: "accepted",
      processedAt: new Date().toISOString(),
    });
  }

  // The SignalR hub: JSON hub protocol over a raw WebSocket.
  server.on("upgrade", (request, socket) => {
    const key = request.headers["sec-websocket-key"];
    const accept = createHash("sha1").update(`${key}${GUID}`).digest("base64");
    socket.write(
      "HTTP/1.1 101 Switching Protocols\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        `Sec-WebSocket-Accept: ${accept}\r\n\r\n`,
    );
    socket.on("error", () => socket.destroy());
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
    const sendFrame = (obj) => {
      if (!socket.destroyed) socket.write(encodeFrame(JSON.stringify(obj) + "\u001e"));
    };
    const welcomeFrame = () => ({
      sessionId: "e2e-session",
      leaseEpoch: 1,
      maxInFlight: 16,
      heartbeatSeconds: 300,
      protocolVersion: "v1",
    });
    const onData = frameReader((opcode, payload) => {
      if (opcode === 8) {
        socket.destroy();
        return;
      }
      if (opcode !== 1) return;
      for (const message of payload.toString("utf8").split("\u001e")) {
        if (message === "") continue;
        const frame = JSON.parse(message);
        if (!("type" in frame)) {
          // The JSON hub protocol handshake `{"protocol":"json","version":1}`
          // carries NO type field: answer `{}` + separator.
          sendFrame({});
        } else if (frame.type === 1) {
          // Hello/Envelope/Heartbeat/Goodbye alike: the hub accepts anything
          // an e2e client invokes (Goodbye rides shutdown — an unacked
          // invocation would hang stop()).
          if (frame.target === "Envelope") {
            const envelope = frame.arguments?.[0];
            handleEventEnvelope(envelope, (status, obj) => {
              void status;
              sendFrame({
                type: 3,
                invocationId: frame.invocationId ?? "0",
                result: { status: obj.status ?? "accepted" },
                error: null,
              });
            });
          } else {
            sendFrame({
              type: 3,
              invocationId: frame.invocationId ?? "0",
              result: frame.target === "Hello" ? welcomeFrame() : true,
              error: null,
            });
          }
        }
        // type 2 = fire-and-forget (Receipt): no response required.
      }
    });
    socket.on("data", onData);
  });

  return new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      resolve({
        url: `http://127.0.0.1:${address.port}`,
        artifacts,
        events,
        close: () =>
          new Promise((r) => {
            for (const socket of sockets) socket.destroy();
            server.close(() => r());
          }),
      });
    });
  });
}

// A port probe proves the fake servers really listen before the adapter dials.
function probe(url) {
  const target = new URL(url);
  const { promise, resolve, reject } = Promise.withResolvers();
  const client = createNetConnect(Number(target.port), target.hostname, () => {
    client.end();
    resolve(undefined);
  });
  client.setTimeout(2000, () => {
    client.destroy();
    reject(new Error(`connect ${url}`));
  });
  client.on("error", reject);
  return promise;
}

const wait = (ms) => {
  const { promise, resolve } = Promise.withResolvers();
  setTimeout(resolve, ms);
  return promise;
};

async function waitFor(what, probeFn, attempts = 100) {
  for (let i = 0; i < attempts; i += 1) {
    const value = await probeFn();
    if (value !== undefined && value !== false) return value;
    await wait(50);
  }
  throw new Error(`timed out waiting for ${what}`);
}

test("inbound photo → artifact → mention event with a dereferenceable ref", async () => {
  const tg = await startTelegram();
  const kernel = await startKernel();
  const dir = mkdtempSync(join(tmpdir(), "tg-e2e-"));
  const { run } = await import("../../dist/index.js");

  try {
    await probe(tg.url);
    await probe(kernel.url);

    const config = {
      kernelUrl: kernel.url,
      enrollSecret: "enroll-dev",
      endpointId: "tg-adapter",
      team: "team-alpha",
      providerInstanceId: "local-telegram",
      botToken: BOT_TOKEN,
      chats: [{ chatId: GROUP, labels: ["ofac", "coding"] }],
      mode: "poll",
      webhookListenPort: 8077,
      tenant: "api.telegram.org",
      statePath: join(dir, "e2e.sqlite3"),
      mediaMaxBytes: 20971520,
      mediaEnabled: true,
      apiBase: tg.url,
      logLevel: "error",
    };
    const logger = {
      debug: () => undefined,
      info: () => undefined,
      warn: (msg, fields) => void [msg, fields],
      error: (msg, fields) => void [msg, fields],
    };
    const handle = await run(config, logger);
    await handle.ready;

    // The published mention envelope (published over the hub ⇒ lands in the
    // fake kernel's event inbox).
    const mention = await waitFor("provider.mention.v1", () =>
      kernel.events.find((env) => env.type === "org.facilitator.event.provider.mention.v1"),
    );

    // Observable 1: exactly ONE request_upload reached the kernel.
    const uploads = kernel.events.filter(
      (env) => env.type === "org.facilitator.command.artifact.request_upload.v1",
    );
    assert.equal(uploads.length, 1, "exactly one request_upload");
    assert.equal(uploads[0].data.workId, undefined, "no workId on the ticket request");

    // Observable 2: the stored blob's sha256 equals the PNG's.
    const artifactId = mention.data.sourceRefs[0].artifactId;
    const stored =
      kernel.artifacts.get(artifactId.replaceAll("-", "")) ?? kernel.artifacts.get(artifactId);
    assert.ok(stored !== undefined, "artifact row exists");
    assert.equal(stored.bytes.length, PNG.byteLength);
    assert.equal(stored.digest, PNG_SHA);

    // Observable 3: the event's ref GETs the exact PNG bytes back through the
    // link-plane content route.
    const fetched = await fetch(
      `${kernel.url}/api/link/v1/artifacts/${artifactId.replaceAll("-", "")}/content`,
    );
    assert.equal(fetched.status, 200);
    assert.deepEqual(new Uint8Array(await fetched.arrayBuffer()), PNG);

    // Observable 4: the delivery id is the update id; the labels + chat ride.
    assert.equal(mention.data.providerDeliveryId, String(UPDATE_ID));
    assert.equal(mention.data.provider, "telegram");
    assert.equal(mention.data.reference.channelOrResourceId, GROUP);
    assert.deepEqual(mention.data.labels, ["ofac", "coding"]);
    assert.equal(mention.data.sourceRefs[0].kind, "chat.attachment");
    assert.equal(mention.data.sourceRefs[0].digest, PNG_SHA);
    assert.match(mention.data.sourceRefs[0].storageUrl, /^artifact:\/\/provider\/uniqE2E\//);
    assert.equal(mention.subject, `provider/telegram/${MESSAGE_ID}`);

    // The photo leg really went over the wire.
    assert.equal(tg.calls.getFile, 1);
    assert.equal(tg.calls.downloads, 1);
    assert.equal(tg.calls.deleteWebhook, 1, "poll mode clears the webhook first");

    await handle.shutdown();
  } finally {
    await kernel.close();
    await tg.close();
    rmSync(dir, { recursive: true, force: true });
  }
});
