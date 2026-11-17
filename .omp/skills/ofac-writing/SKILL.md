---
name: ofac-writing
description: "Write or refine any prose in open-facilitator (docs/, site pages, README, changelog, runbooks, skills, announcements): pick the register first, then apply the repo's one voice law — plain, active, as-built, no hype. Load references/mechanics.md for the full style law (voice, word choice, grammar, punctuation, code/identifier formatting, numbers, budgets), references/registers.md for the per-document-type register, references/verify.md for the delivery checklist, gates, and worked before/after examples. For AGENTS.md signpost structure use ofac-agent-doc-authoring; for diagrams ofac-diagram-style; for capture ofac-doc-gif; for UI strings design-language §24."
---

# Writing law (all prose in this repo)

One voice, several registers. The voice never changes: **plain, concrete,
active, as-built, no hype.** The register (reference, tutorial, how-to,
explainer, runbook, changelog, signpost, consumer page, announcement,
decision record) decides shape, person and length — never tone.

Docs describe **implemented reality**. The moment a slice lands, as-built
replaces design notes; nothing cites implementation-plan status (the drift
gate rejects it in signposts, and it reads as rot everywhere else).

**Conflict rule: repo voice wins.** When an imported framework (plain
language, ASD-STE100, Google developer style, GOV.UK, AP, Orwell) and a
law below disagree, this skill decides; where this skill is silent, the
narrower framework governs its own document kind. Imported frameworks are
kept for their measurable parts — caps, ban lists, ratios, structures.
Their persona theatre (write like a character, cheer the reader up, adopt
a mascot voice) is out of scope here: this repo's persona is the plain one
above, so a style guide that only supplies a personality adds nothing.

## Do this, in order

1. **Classify.** One register per page; a page mixing two is the repo's
   most common docs smell. Say in one line each: audience, reader goal,
   scope, explicit exclusions. Agree the outline before prose.
   `references/registers.md` holds the register table and each register's
   shape; `docs/README.md` "Pick your path" holds the homes.
2. **Write to the budgets.** Sentence, paragraph, heading, list and table
   budgets, identifier formatting and the word ban list are in
   `references/mechanics.md`. Load it when drafting or tightening, not to
   answer a structural question.
3. **Cut.** Every word that does not change what the reader knows or does
   goes. Prefer the short word to the long one; cut a clause before you cut
   a fact; never sacrifice precision where a term is load-bearing.
   Cross-link instead of restating: one canonical statement of a law,
   everything else points at it (same drift law as code).
4. **Verify before claiming done.** `references/verify.md` is the delivery
   checklist plus the gates. A page is not finished until a reader with no
   context can act on it — hand it to a fresh agent session, or re-read
   only the first sentence of each section and ask whether the page still
   tells the story.

## The laws that always apply

- **Front-load.** The first sentence of a page or section states what the
  thing is for, in the reader's terms, and what they get from reading on.
  No throat-clearing, no definitions of the obvious, no "this document
  will describe".
- **The subject acts.** "The routing sink parks the intent and the
  answer's arrival wakes it" — not "intelligently resumes". Name the
  mechanism, not the feeling.
- **No dialogue, no provenance.** Documentation states the law as if it
  were always the law. Never leaks: who asked, who agreed, review narration
  ("we decided", "as discussed", "the reviewer wanted"), implementation
  chatter ("initially we", "later refined to", "kept for compatibility
  with the old X"), question marks answering an imagined reader ("but
  why…?"), or session artifacts (plan names, ticket chatter, `¶`-style
  turns). The diff owns the conversation; the page owns the result. This
  is the same standing rule as the signpost gate's ban on plan-status
  prose, widened to every document.
- **History lives in history files** (CHANGELOG, `.omp/plans/`, audit
  law's own prose). Even there the voice is impersonal record ("Added
  `media-policy`"; not "we added"): a history file may narrate change,
  but still never dialogue. A regular page that catches itself narrating
  change ("recently gained", "IMPLEMENTED", "as of this release") is
  describing its own CHANGELOG — restate the law or move the line.
- **Honest degradation claims.** Say what happens on failure and where the
  evidence lands (audit action name, admin route). "Nothing vanishes
  silently" is a claim only when an audit row backs it.
- **No hype adjectives, no emoji, no exclamation marks.** British-American
  mix stays as the page has it. Consumer-page copy rules are the managed
  `consumer-site-unslop` skill, unchanged; UI strings are
  `design-language` §24, unchanged.
- **One example per mechanism where one suffices.** Exact identifiers
  (event type strings, admin resource ids, audit action names, config keys)
  quoted verbatim so they are greppable.

## Progressive disclosure (this skill's own law)

SKILL.md is the always-loaded core: voice, order of work, conflict rule.
Load a reference only when the task needs it:

| Task | Load |
|---|---|
| drafting or tightening prose, word/number/format questions | `references/mechanics.md` |
| choosing or shaping a document kind | `references/registers.md` |
| finishing pass, gate run, worked before/after examples | `references/verify.md` |

The same law applies to prose you write here: a page states its own law and
links out for detail, and a skill body stays a procedure with on-demand
references rather than an essay. New writing law lands in exactly one of
these three files, never in a second skill (pointers elsewhere).
