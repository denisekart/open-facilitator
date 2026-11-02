// Offline codec tests: framing + frame semantics, no network.
import assert from "node:assert/strict";
import { test } from "node:test";

import { decodeFrame, encodeHandshake, encodeInvocation, FRAME_SEPARATOR, splitFrames } from "../src/signalr.js";

test("handshake frame is protocol json version 1", () => {
  const text = encodeHandshake();
  assert.ok(text.endsWith(FRAME_SEPARATOR));
  assert.deepEqual(JSON.parse(text.slice(0, -FRAME_SEPARATOR.length)), { protocol: "json", version: 1 });
});

test("invocation frame carries target and arguments", () => {
  const encoded = encodeInvocation("7", "Hello", [{ kind: "hello" }]);
  assert.ok(encoded.endsWith(FRAME_SEPARATOR));
  const frame = JSON.parse(encoded.slice(0, -FRAME_SEPARATOR.length));
  assert.equal(frame["type"], 1);
  assert.equal(frame["invocationId"], "7");
  assert.equal(frame["target"], "Hello");
  assert.deepEqual(frame["arguments"], [{ kind: "hello" }]);
});

test("splitFrames keeps the partial tail", () => {
  const stream = '{"type":0}' + FRAME_SEPARATOR + '{"type":1,"ta';
  const { frames, rest } = splitFrames(stream);
  assert.deepEqual(frames, ['{"type":0}']);
  assert.equal(rest, '{"type":1,"ta');
});

test("decode maps handshake, invocation, completion, close", () => {
  assert.deepEqual(decodeFrame('{"type":0}'), { kind: "handshake" });
  assert.deepEqual(decodeFrame('{"type":0,"error":"boom"}'), { kind: "handshake", error: "boom" });
  assert.deepEqual(decodeFrame('{"type":1,"target":"Deliver","arguments":[1,2]}'), {
    kind: "invocation",
    target: "Deliver",
    args: [1, 2],
  });
  assert.deepEqual(decodeFrame('{"type":6,"invocationId":"1","result":null}'), {
    kind: "completion",
    invocationId: "1",
    result: null,
  });
  assert.deepEqual(decodeFrame('{"type":6,"invocationId":"1","error":"nope"}'), {
    kind: "completion",
    invocationId: "1",
    error: "nope",
  });
  assert.deepEqual(decodeFrame('{"type":7}'), { kind: "close" });
});

test("ack and ping frames decode to null", () => {
  assert.equal(decodeFrame('{"type":4,"invocationId":"1"}'), null);
  assert.equal(decodeFrame('{"type":8}'), null);
});

test("a typeless frame is the kernel's no-subprotocol handshake ack", () => {
  assert.deepEqual(decodeFrame("{}"), { kind: "handshake" });
});

test("a type-3 result frame decodes as a completion", () => {
  assert.deepEqual(decodeFrame('{"type":3,"invocationId":"1","result":{"sessionId":"s"}}'), {
    kind: "completion",
    invocationId: "1",
    result: { sessionId: "s" },
  });
  assert.equal(decodeFrame('{"type":3,"invocationId":"1","item":1}'), null);
});
