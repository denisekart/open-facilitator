import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { startFakeProvider, type FakePlane } from "@ofac/scenario-plane";
import { TelegramClient } from "@ofac/telegram-adapter";
import { MattermostClient } from "@ofac/mattermost-adapter";
import { GitHubClient } from "@ofac/github-adapter";
import { GitLabClient } from "@ofac/gitlab-adapter";

// The gate's T2 lane: the adapters' OUTBOUND legs over real HTTP against
// the fake provider plane - the shapes and failure modes the F6/B4/...
// scenarios inject. Inbound legs (webhook -> adapter -> kernel) are the
// adapters' own vitest tiers plus the kernel's T1 ingress tests; this file
// proves the plane is a faithful provider for the legs a scenario needs.
let plane: FakePlane;

beforeAll(async () => {
  plane = await startFakeProvider();
});
afterAll(async () => {
  await plane.close();
});

async function mode(family: string, mode: string): Promise<void> {
  const r = await fetch(`${plane.url}/__plane/mode`, {
    method: "POST",
    body: JSON.stringify({ family, mode }),
  });
  expect(r.status).toBe(200);
}

async function state(): Promise<{ received: { family: string; path: string }[] }> {
  return (await fetch(`${plane.url}/__plane/state`)).json() as Promise<{
    received: { family: string; path: string }[];
  }>;
}

describe("telegram leg", () => {
  it("sendMessage rides /bot<token>/sendMessage and returns the plane's message id", async () => {
    const client = new TelegramClient(plane.url, "123:token");
    const sent = await client.sendMessage({ chat_id: -1001234567890, text: "hello" });
    expect(sent.message_id).toBeGreaterThan(0);
    const st = await state();
    expect(st.received.some((r) => r.family === "telegram" && r.path.includes("sendMessage"))).toBe(
      true,
    );
  });

  it("a vanished conversation answers the family's honest error, never a hang", async () => {
    await mode("telegram", "vanished");
    const client = new TelegramClient(plane.url, "123:token");
    await expect(client.sendMessage({ chat_id: -1, text: "x" })).rejects.toThrow(/chat not found/);
    await mode("telegram", "ok");
  });
});

describe("mattermost leg", () => {
  it("createPost posts to /api/v4/posts and returns the post id", async () => {
    const client = new MattermostClient(plane.url, "tok", undefined);
    const id = await client.createPost("channel-1", "hello");
    expect(id).toBeTruthy();
    const st = await state();
    expect(st.received.some((r) => r.family === "mattermost" && r.path === "/api/v4/posts")).toBe(
      true,
    );
  });
});

describe("github leg", () => {
  it("postComment rides /repos/<repo>/issues/<n>/comments", async () => {
    const client = new GitHubClient(plane.url, "tok");
    await client.postComment("o/r", 7, "hi");
    const st = await state();
    expect(
      st.received.some(
        (r) => r.family === "github" && r.path.includes("/repos/o/r/issues/7/comments"),
      ),
    ).toBe(true);
  });

  it("a 403 wall surfaces as the typed error with the status", async () => {
    await mode("github", "403");
    const client = new GitHubClient(plane.url, "tok");
    await expect(client.postComment("o/r", 7, "hi")).rejects.toThrow(/403/);
    await mode("github", "ok");
  });
});

describe("gitlab leg", () => {
  it("createIssue rides /api/v4/projects/<repo>/issues", async () => {
    const client = new GitLabClient(plane.url, "tok");
    await client.createIssue("grp/proj", "title", "body", "owner");
    const st = await state();
    expect(st.received.some((r) => r.family === "gitlab" && r.path.includes("/issues"))).toBe(true);
  });
});
