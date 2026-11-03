/**
 * Minimal, dependency-free Markdown for chat message rendering. The whole
 * input is HTML-escaped FIRST, then a closed set of transforms is applied
 * (fenced code blocks, inline code, bold, emphasis, links), so the output is
 * construction-safe for `{@html}`: only tags this function emits exist in it.
 * Line breaks stay literal — the container carries `whitespace-pre-wrap`.
 */

const ESCAPES: [RegExp, string][] = [
  [/&/g, "&amp;"],
  [/</g, "&lt;"],
  [/>/g, "&gt;"],
];

const CODE_BLOCK = '<pre class="mono mt-1 mb-1 overflow-x-auto rounded bg-bg p-2 text-xs">$1</pre>';
const CODE_INLINE = '<code class="mono rounded bg-bg px-1 text-[0.92em]">$1</code>';
const STRONG = "<strong>$1</strong>";
const EMPHASIS = "<em>$1</em>";
const LINK =
  '<a class="text-accent underline" href="$2" target="_blank" rel="noopener noreferrer">$1</a>';

export function renderChatMarkdown(text: string): string {
  const escaped = ESCAPES.reduce((acc, [from, to]) => acc.replace(from, to), text);
  return escaped
    .split("```")
    .map((segment, index) => (index % 2 === 1 ? renderCodeBlock(segment) : renderInline(segment)))
    .join("");
}

/** Odd segments sit between fences: render as a code block, dropping an
 * optional single-line language hint and the fence's own newlines. An
 * unclosed fence still renders as code (graceful degradation). */
function renderCodeBlock(segment: string): string {
  const body = /^[a-zA-Z0-9+#._-]*\n/.test(segment)
    ? segment.replace(/^[a-zA-Z0-9+#._-]*\n/, "")
    : segment;
  return CODE_BLOCK.replace("$1", body.replace(/^\n+|\n+$/g, ""));
}

function renderInline(segment: string): string {
  return segment
    .replace(/`([^`\n]+)`/g, CODE_INLINE)
    .replace(/\*\*([^*\n]+)\*\*/g, STRONG)
    .replace(/\*([^*\n]+)\*/g, EMPHASIS)
    .replace(/\[([^\]\n]+)\]\((https?:[^)\s]+)\)/g, LINK);
}
