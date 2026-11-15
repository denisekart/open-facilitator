---
name: ofac-agent-doc-authoring
description: "Write and keep honest AGENTS.md signposts and agent-facing docs (docs/getting-started-agents.md, skills, machine-readable pointers) in open-facilitator: what a signpost may contain, the 80-line cap and path-resolution law enforced by agents-docs-check, and how to distill area knowledge for agent readers."
---

# Agent-facing docs: signposts and the machine reader

Two distinct artifacts, one law (every claim resolves in the tree):

- **`AGENTS.md` signposts** — per-area, for the agent *working in* the area.
- **`docs/getting-started-agents.md`** — the repo-wide agent walkthrough:
  auth, the HTTP surface, link enrollment, contract locations, error
  contract. It is *consumer doc that happens to address an agent*, so it
  follows ofac-doc-authoring too; a signpost is *operational law*.

## Signpost law (what `make docs-check` enforces, learned the hard way)

`protocol/tools/agents-docs-check.mjs` walks every `AGENTS.md`:

- **≤ 80 lines total.** Real cap, counted as lines. When adding an anchor
  would overflow: merge clauses onto existing bullets, drop what the code
  already says, or move the content to a skill. Do not shrink prose to
  fragments; delete scope.
- **Every backticked path-like token and every relative link target must
  exist** (worktree-root or doc-relative). Placeholders with globs/`<>` are
  safe; concrete stale paths are not. After a refactor that moves files,
  the signpost edits ride the SAME commit.
- **No implementation-plan status prose** (`Plan 3`, "landed this week").
  Status lives in `.omp/plans/`; a signpost states as-built law only.
- **No dialogue or provenance** (the ofac-doc-authoring law applies here
  with extra force — a signpost is read cold by an agent with no memory
  of the discussion): no "we agreed", no "kept because X asked", no
  decision narration. Law and anchors only.
- **Every `src/<area>/` (and `tests/Facilitator.Tests`, `reference`,
  `features`) needs its own `AGENTS.md`** — a new area lands one in the
  same commit.

## What a signpost contains (in order)

1. `## Purpose` — one breath, what the area is for.
2. `## Truth anchors` — bullets of `path — the law it owns`. This is the
   whole point: the reader should not have to grep to find which file owns
   which invariant. One line per anchor; name the mechanism + the
   invariant a change must not break (fences, ledgers, cap laws).
3. `## Develop here` — the commands that actually gate this area.
4. `## Invariants` — the few rules invisible in any single file.

Never: restated behavior the code shows, tutorial prose, tables of public
API (that is `docs/protocol.md`), changelog narration.

## For the agent-walkthrough page

An agent reads to *act*: every section must name a concrete endpoint,
header, or file, and the exact failure shape. "Authentication" is wrong;
"`Authorization: Bearer <token>`; a wrong-team token is 403
`forbidden-team`, not 401" is right. Machine-readable contract locations
are a table of schema/OpenAPI paths, never descriptions of them.

## Skills as agent docs

A procedure an agent executes belongs in `.omp/skills/<name>/SKILL.md`
(frontmatter `name` + `description` with the trigger conditions; body =
steps + gotchas learned the hard way, no narrative). Docs a human reads
belongs in `docs/` or the site (ofac-doc-authoring). If a doc says "when
X, do Y, Z, then check W", it is a skill.
