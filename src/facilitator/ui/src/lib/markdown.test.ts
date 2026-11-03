import { describe, expect, it } from "vitest";

import { renderChatMarkdown } from "./markdown.js";

describe("renderChatMarkdown", () => {
  it("escapes raw html so model output cannot inject markup", () => {
    const html = renderChatMarkdown("<img src=x onerror=alert(1)> **hi**");
    expect(html).not.toContain("<img");
    expect(html).toContain("&lt;img");
    expect(html).toContain("<strong>hi</strong>");
  });

  it("renders fenced blocks verbatim, dropping the language hint", () => {
    const html = renderChatMarkdown("before\n```csharp\nvar x = 1 < 2;\n```\nafter");
    expect(html).toContain("<pre");
    expect(html).toContain("var x = 1 &lt; 2;");
    expect(html).not.toContain("csharp");
    expect(html.startsWith("before")).toBe(true);
    expect(html.endsWith("after")).toBe(true);
  });

  it("renders an unclosed fence as code instead of losing the text", () => {
    expect(renderChatMarkdown("```no fence close")).toContain("<pre");
  });

  it("renders inline code, bold, emphasis and http(s) links", () => {
    const html = renderChatMarkdown(
      "`workId` is **done** with *care* — see [timeline](https://k/api/works/1/timeline)",
    );
    expect(html).toContain("<code");
    expect(html).toContain("<strong>done</strong>");
    expect(html).toContain("<em>care</em>");
    expect(html).toContain('href="https://k/api/works/1/timeline"');
    expect(html).toContain('rel="noopener noreferrer"');
  });

  it("leaves non-http link targets as plain text", () => {
    expect(renderChatMarkdown("[x](javascript:alert(1))")).not.toContain("<a");
  });
});
