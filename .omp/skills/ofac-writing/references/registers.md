# Registers: one kind of help per page

The tree already maps the reader needs (this repo's Diátaxis mapping,
extended to the kinds that live here too). Pick one row; write only that
kind; cross-link the others.

| Register | Reader stance | Repo home |
|---|---|---|
| tutorial (teach) | "I have never done this" | `docs/getting-started.md`, `docs/getting-started-agents.md` |
| how-to (task) | "I have a job to finish" | `docs/advanced.md` sections, `docs/runbooks/` |
| reference (exact detail) | "I am looking something up" | `docs/protocol.md`, `protocol/json-schema/`, `protocol/openapi/`, admin resource tables |
| explanation (understanding) | "I want to know why" | `docs/overview.md`, `site/src/pages/what.md`, `docs/security.md` rationale prose |
| runbook (operate) | "Something is happening now" | `docs/runbooks/` |
| changelog (history) | "What changed" | the repo changelog file (`CHANGELOG.md`, once it lands); until then, release notes |
| signpost (agent in the area) | "I am editing here" | the area's `AGENTS.md` (ofac-agent-doc-authoring owns structure) |
| skill (agent procedure) | "I must execute this" | `.omp/skills/<name>/SKILL.md` |
| consumer page | "Convince me it is worth it" | `site/src/pages/*` (managed `consumer-site-unslop`) |
| announcement (comms) | "Tell me the news" | release notes; a `docs/announcements/` home appears with the first one |
| business prose (status, incident, update) | "Where do we stand" | reports and notes handed to a reader who must act |
| decision record | "Why is it this way" | `.omp/plans/`, ADR-shaped notes; never a law page |

Placement of anything not listed: consumer-facing system truth →
`docs/` (a new page joins both the "Pick your path" table and "The
documents" in `docs/README.md`); entry-point summary → root `README.md`;
repeatable procedure → a skill.

Per-register shape (voice laws from SKILL.md apply to all of these):

## Reference
Person-free or "the caller". Present tense, exact, complete, ordered by
the artifact it mirrors (schema/wire/entity order), never by narrative.
Every row resolvable: identifiers verbatim, failure shapes named (code +
body). No persuasion, no background paragraph, no "note that". A reader
must be able to paste a value from it. Machine-readable contract
locations are a table of schema/OpenAPI paths, never descriptions of
them.

## Tutorial
Second person, one path, one outcome, every step concrete and every
expected result shown ("`work-id` printed; a 401 here means the token is
for another team"). Sequences numbered; nothing optional (optionality is
a how-to). State prerequisites before step 1. Do not teach two tasks in
one tutorial, and do not explain design in the middle of a step — link
the explainer.

## How-to
Task named in the title, goal restated in one sentence, steps imperative,
troubleshooting at the end. Assumes competence: no "first, open a
terminal". Variants are separate short subsections, not a branchy
paragraph.

## Explanation
Prose-first; lists and tables only for enumerations. Argues from the
problem to the mechanism, names trade-offs, states what the design gives
up. Analogies once, not as a crutch, and never as a substitute for the
mechanism. No step numbers.

## Runbook
Situation, detection (query, metric, admin route, audit action name),
actions as imperative numbered steps, escalation and evidence
location. Telegraphic is correct here — a reader mid-incident does not
want connective tissue. Every command copy-pastable.

## Changelog
Impersonal record of change: "Added `media-policy`", "Removed the v1
enroll route"; never "we", never rationale (link the decision record),
never marketing. Same verb tense throughout an entry.

## Signpost
Law and anchors only, as-built, no status prose, ≤ 80 lines; structure is
`ofac-agent-doc-authoring`'s law. The prose laws of this skill apply with
extra force: a signpost is read cold by an agent with no memory of any
discussion.

## Skill
Body = steps + gotchas learned the hard way, no narrative; frontmatter
`name` + `description` carrying the trigger conditions; heavy detail in
`references/` loaded on demand. If a document says "when X, do Y, Z,
then check W", it is a skill, not a doc.

## Consumer page
Managed `consumer-site-unslop` rules govern: no source paths, no
toolchain, no licensing. Voice laws still bind — no hype adjectives, no
emoji; the page may sell, it may not lie, and every claim still resolves
to a `docs/` page.

## Announcement
Lead with the change and what the reader can now do; each paragraph adds
less-critical detail so it can be cut from the bottom. Feature claims
name the mechanism or link the reference page. No exclamation marks, no
gratitude ("we're thrilled"), no roadmap promises about unwritten code.

## Business prose (status report, incident note, stakeholder update)
The reader needs the position and the ask, so: outcome or ask first,
supporting facts after, next step named with an owner and a date. Plain
language binds hardest here — these pages are read by people who will act
on them under time pressure. No hedging ladder ("we would respectfully
suggest the possibility of"): say what should happen. Numbers, not
adverbs ("3 of 5 walks green", not "mostly working"). First person is
allowed here for the writer's own commitments ("I will ship the fix by
Friday") and nowhere else.

## Decision record
Situation, options, decision, consequences, in past tense for the deciding
and present tense for the resulting law. Provenance is welcome here —
this is the one home where "we chose X over Y because Z" belongs. It is
still not a chat log: no turn-by-turn narration, no participant names
where a role suffices.
