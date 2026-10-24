// Outbound envelope builder + enrollment helper (mock fetch) proofs.
import { describe, expect, it } from "vitest";
import { validateEnvelope } from "@ofac/protocol";
import { buildEvent, dataschemaFor, enroll, EnrollmentError } from "../../src/index.js";

describe("buildEvent", () => {
  it("produces a schema-valid CloudEvent with derived dataschema", () => {
    const env = buildEvent("org.facilitator.event.work.progress.v1", {
      source: "urn:workspace:linkd/gw-1",
      subject: "work/w-1/assignment/a-1",
      data: { workId: "w-1", assignmentId: "a-1", executionGeneration: 1, progressNote: "hello" },
    });
    expect(validateEnvelope(env)).toEqual({ ok: true });
    expect(env.dataschema).toBe(dataschemaFor("org.facilitator.event.work.progress.v1"));
    expect(env.id).toMatch(/^[0-9a-f-]{36}$/);
    expect(env.time).toMatch(/Z$/);
  });

  it("passes traceparent through when given", () => {
    const env = buildEvent("org.facilitator.event.work.accepted.v1", {
      source: "urn:workspace:linkd/gw-1",
      subject: "work/w-1",
      traceparent: "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
      data: { workId: "w-1", assignmentId: "a-1", acceptedAt: "2026-10-22T18:00:01Z" },
    });
    expect(env.traceparent).toMatch(/^00-/);
    expect(validateEnvelope(env)).toEqual({ ok: true });
  });

  it("throws a typed error for an envelope the client itself would reject", () => {
    expect(() =>
      buildEvent("org.facilitator.event.work.progress.v1", { source: "notauri", subject: "s" }),
    ).toThrow(/envelope-violation|schema-violation/);
  });
});

describe("enroll", () => {
  it("POSTs the request and returns the typed response", async () => {
    const calls: { url: string; body: Record<string, unknown> }[] = [];
    const response = await enroll(
      {
        baseUrl: "http://fac:8088/",
        token: "enroll-dev",
        endpointId: "gw-1",
        instanceId: "inst-1",
        trustMode: "isolated-executor",
      },
      (async (url, init) => {
        calls.push({
          url: String(url),
          body: JSON.parse(String(init?.body)) as Record<string, unknown>,
        });
        return new Response(
          JSON.stringify({
            enrollmentId: "enr-1",
            trustMode: "isolated-executor",
            linkToken: "tok-1",
            expiresAt: "2026-11-01T00:00:00Z",
          }),
          { status: 201, headers: { "content-type": "application/json" } },
        );
      }) as typeof fetch,
    );
    expect(calls[0]!.url).toBe("http://fac:8088/api/link/v1/enroll");
    expect(calls[0]!.body).toEqual({
      token: "enroll-dev",
      endpointId: "gw-1",
      instanceId: "inst-1",
      trustMode: "isolated-executor",
    });
    expect(response.linkToken).toBe("tok-1");
  });

  it("401 becomes a typed EnrollmentError carrying the ProblemDetails", async () => {
    await expect(
      enroll(
        {
          baseUrl: "http://fac:8088",
          token: "wrong",
          endpointId: "gw",
          instanceId: "i",
          trustMode: "shared-runtime",
        },
        (async () =>
          new Response(
            JSON.stringify({
              type: "https://errors.openfacilitator.org/enrollment-secret-invalid",
              title: "invalid secret",
              status: 401,
            }),
            { status: 401, headers: { "content-type": "application/problem+json" } },
          )) as typeof fetch,
      ),
    ).rejects.toMatchObject({
      status: 401,
      problem: { type: "https://errors.openfacilitator.org/enrollment-secret-invalid" },
    });
    await expect(
      enroll(
        {
          baseUrl: "http://f",
          token: "w",
          endpointId: "gw",
          instanceId: "i",
          trustMode: "shared-runtime",
        },
        (async () => new Response("", { status: 401 })) as typeof fetch,
      ),
    ).rejects.toBeInstanceOf(EnrollmentError);
  });
});
