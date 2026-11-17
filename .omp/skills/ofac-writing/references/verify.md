# Verify: checklist, gates, worked examples

## Before calling a page done

- One register; the page does not drift into another halfway through.
- First sentence of the page and of every section states what it is for;
  headings alone still tell the story.
- Style law holds (`references/mechanics.md`): budgets, voice, word
  choice, punctuation, numbers.
- No banned word, no hype adjective, no exclamation mark, no emoji, no
  "we"/"our" for the project, no dialogue or provenance leak, no
  plan-status prose, no self-narration ("recently gained", "IMPLEMENTED").
- Every identifier backticked verbatim and greppable; every code block
  real (run it or diff it against the source of truth).
- Every relative link and backticked worktree path resolves — the gate
  checks AGENTS.md; check `docs/` and `site/` yourself: `grep` the
  targets.
- Tables: ≥ 3 items × ≥ 2 facets, key column resolvable, no prose
  restating the table above it.
- Failure and degradation stated with its evidence location, not
  promised.
- Cold read: a reader with no context (or a fresh agent session with only
  this page) can act on it. If the page explains a mechanism that exists
  nowhere, it is prose about vapor.

## Gates

1. `make docs-check` — signpost caps, dangling paths, plan-status prose.
2. Diagrams: `ofac-diagram-style` rules + the managed
   `mermaid-doc-validation` skill (real `mermaid.parse`, HEAD baseline).
3. Site pages: `cd site && npm run build` must stay clean (33-page count
   in its output is the baseline; a new page raises it legitimately).
4. Doc-sync rule: a change that lands a schema/endpoint/table updates the
   matching signpost/skill in the SAME commit.

## Worked examples

Before/after pairs, taken from the style authorities this style synthesizes
(cited below) plus the repo's own pages. Match your draft against the
pattern, not the sentence.

**Buried logic and Latinates** (plainlanguage.gov playbook):
> Before: "Pursuant to the provisions of the aforementioned policy, it is
> incumbent upon the employee to ensure that all expenditures incurred
> during the course of business travel are properly documented and
> submitted in a timely manner for reimbursement."
> After: "Submit your travel expenses within 30 days of your trip. Keep
> all receipts. We process requests within two weeks."

**Passive nominalisation hides the cause** (plainlanguage.gov playbook):
> Before: "The failure of the application to launch can be attributed to
> a misconfiguration in the environment settings, which was introduced
> during the most recent deployment."
> After: "The app won't start because the latest deployment
> misconfigured the environment. We have rolled back the change and are
> redeploying now."

**Hedged imperative, unquantified** (ASD-STE100 playbook):
> Before: "It is recommended that technicians should carefully inspect
> the aforementioned component assembly for any potential signs of wear
> prior to system operation."
> After: "Inspect the component assembly for wear before you operate the
> system."

**Condition placed after the action, weak verb** (ASD-STE100 playbook):
> Before: "When the pressure drops too low, you might want to consider
> resetting the main system valve."
> After: "If the pressure is low, reset the main valve."

**Future tense and filler in API prose** (Google developer style playbook):
> Before: "In order to initialize our SDK, users are required to first
> invoke the setup method, after which time you will be able to make API
> requests successfully."
> After: "To initialize the SDK, call `setup()`. Once initialized, you
> can make API requests."

**"Simply" sells, mechanics tell** (Google developer style playbook):
> Before: "Simply add the configuration dictionary to your project and
> you're good to go!"
> After: "Add the configuration dictionary to your project configuration
> file."

**Hype adverb instead of mechanism** (this repo's voice law,
`docs/overview.md` lineage):
> Before: "The link service intelligently resumes delivery once the agent
> reconnects."
> After: "The routing sink parks the intent and the answer's arrival
> wakes it."

**Provenance leak on a law page** (signpost gate law, widened):
> Before: "Initially we used a token, later refined to a challenge/proof
> pair, and after review we decided to keep rotation manual."
> After: "Rotation is manual: the rotate mutation mints the second
> credential and revokes the first on first use."

**Announcement that leads with feeling** (AP playbook):
> Before: "We are thrilled to announce that our incredible company has
> successfully raised an amazing $25 million, which will enable us to
> continue our incredible journey of transforming the industry!"
> After: "Aurora Labs has raised $25 million in Series B funding, the
> company said Thursday. The round was led by Meridian Capital with
> participation from existing investors."

**Editorializing in a factual note** (AP playbook):
> Before: "5 employees were shocked and saddened as the company announced
> layoffs affecting over 30% of the workforce, in what many see as a
> disastrous strategic decision."
> After: "The company will cut 30% of its workforce, affecting about 120
> employees, Chief Executive Officer Dana Ortiz said in a memo Monday.
> The layoffs take effect Sept. 1."

**Link text** (GOV.UK playbook):
> Before: "To learn more about how the enrollment flow works, click
> here."
> After: "The [enrollment flow](docs/protocol.md#enroll) lists every
> route, header, and failure code."

**Headings** (Google developer style): "Configuring authentication" or
"What happens when a delivery fails", never "Authentication overview",
"Misc", or "Some notes".

**Numbers** (AP + Google): "five workers", "12 steps", "a 3-second
timeout", "6%", "2,000 events"; "Forty percent of the files…" when a
count opens the sentence.

## Where a law lives

Writing law lives in this skill only. `ofac-agent-doc-authoring` owns
signpost structure and the gate mechanics; `design-language` §24 owns UI
strings; `consumer-site-unslop` owns consumer pages; `ofac-diagram-style`
owns figures. Those files point here for prose law; nothing duplicates it.
