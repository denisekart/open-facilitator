# Style law: mechanics, grammar, formatting, budgets

The repo style is a synthesis of what has provably worked at scale:
Google developer documentation style (second person, present tense,
task headings), Microsoft Writing Style (sentence-case headings, everyday
words, contractions allowed), GOV.UK (plain words, front-loaded links,
numbers as numerals), plainlanguage.gov / the Plain Writing Act (one-read
comprehension), ASD-STE100 (sentence caps, one meaning per word in
instructions), AP (numbers, dates, no editorializing), and Diátaxis (one
reader need per page). Where they conflict, this page decides — repo
voice wins.

## Voice rules

1. Active voice: the subject acts. "The sink parks the intent", not "the
   intent is parked". Passive only when the actor is genuinely unknown or
   irrelevant ("the row is keyed by `work-id`"). Under ~10% of sentences.
2. Present tense for system behavior ("`POST /link/enroll` returns a
   challenge"); never future for deterministic behavior ("will return").
   Imperative for instructions ("Run `make docs-check`").
3. Person: tutorials, how-tos, runbooks, announcements address the reader
   as "you". Reference and signpost prose stays person-free ("the
   caller"). Never "we"/"our" for the project, never "users should" —
   name the actor or say "you".
4. Do what the reader needs, not what the writer enjoys: answer the
   question the heading poses, in the first sentence under it.
5. Contractions are fine where the sentence is ordinary prose ("it's",
   "don't") — they keep the tone plain; skip them in terse table cells
   and legal-adjacent statements.

## Word choice

1. Everyday words: "use" not "utilize", "start" not "commence", "to" not
   "in order to", "if" not "in the event that", "about" not "with regard
   to". A longer word earns its place only by meaning more.
2. One word, one meaning per page: if "session" means a JWT session, it
   never also means a UI visit. Polysemous jargon ("handle", "fixed",
   "token") gets a precise synonym or a definition at first mention.
3. Cut every word that does not change what the reader knows or does.
   Strip intensifiers (very, really, quite), hedges (somewhat, sort of,
   arguably), and filler openers ("Note that", "It should be mentioned
   that", "As previously stated").
4. Strong verbs over nominalisations: "the router retries delivery", not
   "the router performs retry of the delivery". No "there is/there are"
   to start a sentence unless listing.
5. Vague modifiers ("fast", "extreme", "normal", "large") quantify or
   die: say the number or cut the claim.
6. Define repo-invented terms (`work`, `intent`, `sink`) once, at first
   canonical mention, then cross-link. Expand acronyms at first mention
   unless daily-drivers (API, URL, HTTP, JWT). Never abbreviate as a
   verb ("use SSH to connect", not "ssh into").
7. No editorializing: "clearly", "unfortunately", "notably",
   "importantly" state the writer's feelings, not the system's facts.

## Grammar and syntax

1. One idea per sentence. Procedural sentences ≤ 20 words; descriptive
   ≤ 25 hard; target average 15–20.
2. One topic per paragraph; 3–5 sentences max (tutorial/runbook steps:
   1–2). A paragraph that needs "however" mid-way is two paragraphs.
3. Conditional clauses come first: "If the token is expired, re-enroll."
4. Noun clusters ≤ 3 nouns ("delivery retry policy", not "link delivery
   retry backoff policy clock").
5. "This/it/that" never stands alone as a subject — attach the noun
   ("this request", "the challenge").
6. Parallel shape everywhere it costs nothing: list items, headings in a
   sequence, paired steps all start the same kind of word.
7. Pronoun–antecedent agreement beats proximity; when in doubt, repeat
   the noun. Repetition is free; ambiguity is not.

## Punctuation and formatting

1. Serial comma in lists ("auth, enrollment, and metrics").
2. Sentence-case headings, no trailing punctuation, max three nesting
   levels (a fourth means a new page). Headings state outcomes or tasks:
   "Rotating a credential", not "Authentication overview" or "Misc".
3. Hyphens: compound modifiers before a noun ("read-through cache"),
   none after ("the cache is read through"). Ranges use "to" in prose
   ("500 to 900"); hyphen only inside tables or numeral ranges
   (`400-499`).
4. Dashes: an em dash can set off a clause, one per paragraph max;
   otherwise commas or parentheses. No spaced hyphen-as-dash.
5. Quotation marks for UI strings and verbatim messages; backticks are
   for identifiers, not for emphasis. Italics for nothing technical.
6. Bullets: parallel shape; no end punctuation unless items are
   multi-sentence (then every item ends with a full stop); no semicolons
   at line ends. Numbered steps only when order is load-bearing; each
   step one imperative sentence ending with a full stop, naming what
   "done" looks like when not obvious. Nest one level max.
7. Apostrophes: possessives normal ("the token's expiry"); no greengrocer
   apostrophes (`AGENTS.md`s, not `AGENTS.md`'s).

## Code, identifiers, links

1. Identifiers, paths, commands, header names, config keys, event type
   strings, status codes: backticked verbatim, greppable, never
   pluralized or case-mangled (`link.enroll.challenge`, not "enroll
   challenges").
2. Code blocks complete enough to run (imports/setup in, expected output
   or failure shape stated) and verified against reality, not memory; a
   block over ~20 lines moves its explanation to prose around it.
3. Links front-load the destination's key term and say what is there:
   "[the enrollment endpoints](docs/protocol.md#enroll)", never "click
   here", never a bare URL as text. Cross-link instead of restating: one
   canonical statement of a law, everything else points at it.

## Numbers, dates, units

1. Spell out one to nine in prose; numerals for 10+; always numerals for
   versions, ports, limits, measurements, percentages (50%), file sizes
   (KB under 1 MB, MB above, ≤ 2 decimals). Mixed sentence: numerals for
   all once any number is 10+.
2. Spell out a number that opens a sentence. Ordinals in prose spell out
   ("first", "third"); ordered-list steps are plain numerals.
3. Dates: 4 June 2017. Commas over 999 (1,000). Decimals keep the
   leading zero (0.5, not .5).

## Tables

1. A table earns its width at ≥ 3 items × ≥ 2 parallel facets; two rows
   is prose.
2. One key column (path, identifier, name); every cell fact, not
   commentary; no prose paragraph above it restating it.
3. Reference tables follow the wire/entity order (`docs/protocol.md`
   registry rows mirror the schema files), never alphabetical
   convenience.

## Banned words

leverage, seamless(ly), robust, powerful, cutting-edge, simply, just,
easily, merely, obviously, clearly, notably, importantly, exciting,
game-changer, "in order to", utilize, commence, pursuant to,
aforementioned, "in the event that", "with regard to", "one-stop shop",
hub, portal, best-in-class, synergy, "we're thrilled".

## Readability targets

- One-read test: a competent new-joiner gets it without re-reading.
- Flesch Reading Ease ≥ 60 for prose sections (tutorials 65–75);
  reference/API prose exempt — precision outranks the score wherever a
  term is load-bearing.
- Jargon test: every term either defined at first canonical mention or
  greppable to a definition.
- Skim test: page title + headings + first sentence of each section,
  read alone, still tell the whole story.
