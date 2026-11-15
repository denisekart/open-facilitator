---
name: ofac-doc-authoring
description: "Write or refine open-facilitator documentation (docs/, site pages, README): pick the document type first, place it in the right tree, keep the repo's honest as-built voice, and pass the doc gates. For AGENTS.md signposts and agent-facing docs use ofac-agent-doc-authoring; for diagrams ofac-diagram-style; for capture demos ofac-doc-gif."
---

# open-facilitator documentation authoring

Docs describe **implemented reality**. The moment a slice lands, as-built
replaces design notes; nothing cites implementation-plan status (the drift
gate rejects it in signposts, and it reads as rot everywhere else).

## 1. Classify before writing

Pick one purpose per page; a page mixing two is the repo's most common docs
smell. The tree already maps the four reader needs:

| Reader need | Repo home |
|---|---|
| tutorial (teach) | `docs/getting-started.md`, `docs/getting-started-agents.md` |
| how-to (task) | `docs/advanced.md` sections, `docs/runbooks/` |
| reference (exact detail) | `docs/protocol.md`, `protocol/json-schema/`, `protocol/openapi/`, admin resource tables |
| explanation (understanding) | `docs/overview.md`, `site/src/pages/what.md`, `docs/security.md` rationale prose |

Before drafting, state in one line each: audience, reader goal, scope,
explicit exclusions. Agree on the outline before prose.

## 2. Place it

| Content | Goes in |
|---|---|
| consumer-facing system truth | `docs/` (map: `docs/README.md` "Pick your path" — a new page must join both that table and "The documents") |
| marketing-leaning product story | `site/src/pages/*.md` — apply the managed `consumer-site-unslop` skill: no source paths, no toolchain, no licensing |
| per-area invariants + anchors | the area's `AGENTS.md` (see ofac-agent-doc-authoring) |
| repeatable procedure/tribal knowledge | a `.omp/skills/<name>/SKILL.md` (audience test: a document a human reads vs. a procedure an agent executes) |
| entry-point summary | root `README.md` |

## 3. Voice laws (all of docs/)

- Dry, concrete, present tense; name the mechanism, not the feeling. "The
  routing sink parks the intent and the answer's arrival wakes it" — not
  "intelligently resumes".
- Honest degradation claims: say what happens on failure and where the
  evidence lands (audit action name, admin route). "Nothing vanishes
  silently" is a claim only when an audit row backs it.
- One example per mechanism where one suffices; exact identifiers (event
  type strings, admin resource ids, audit action names) quoted verbatim so
  they are greppable.
- **No dialogue, no provenance.** Documentation states the law as if it
  were always the law. Never leaks: who asked, who agreed, review
  narration ("we decided", "as discussed", "the reviewer wanted"),
  implementation chatter ("initially we", "later refined to", "kept for
  compatibility with the old X"), question marks answering an imagined
  reader ("but why…?"), or session artifacts (plan names, ticket chatter,
  `¶`-style turns). The diff owns the conversation; the page owns the
  result. This is the same standing rule as the signpost gate's ban on
  plan-status prose, widened to every document.
- **History lives in history files** (CHANGELOG, `.omp/plans/`, audit
  law's own prose). Even there the voice is impersonal record ("Added
  `media-policy`"; not "we added"): a history file may narrate change,
  but still never dialogue. A regular page that catches itself
  narrating change ("recently gained", "IMPLEMENTED", "as of this
  release") is describing its own CHANGELOG — restate the law or move
  the line.
- No hype adjectives, no emoji, British-American mix stays as the page has
  it; copy rules for consumer pages are the managed `consumer-site-unslop`
  skill, unchanged.

## 4. Structure laws

- First sentence of a page (or section) states what the thing is for, in
  the reader's terms.
- Tables beat prose for enumerations of ≥3 items with parallel shape.
- Reference sections follow the wire/entity order (type registry rows in
  `docs/protocol.md` mirror the schema files' order).
- Cross-link instead of restating: one canonical statement of a law,
  everything else links to it (the same drift law as code).

## 5. Verify before claiming done

1. `make docs-check` — signpost caps, dangling paths, plan-status prose.
2. Every relative link and backticked worktree path resolves (the gate
   checks AGENTS.md; check docs/ and site/ yourself: `grep` the targets).
3. Diagrams: `ofac-diagram-style` rules + the managed
   `mermaid-doc-validation` skill (real `mermaid.parse`, HEAD baseline).
4. Site pages: `cd site && npm run build` must stay clean (33-page count in
   its output is the baseline; a new page raises it legitimately).
5. Doc-sync rule: a change that lands a schema/endpoint/table updates the
   matching signpost/skill in the SAME commit.
