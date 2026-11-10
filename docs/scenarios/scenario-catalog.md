# Scenario catalog — who can do what, and what they should see

**Purpose.** An inventory of interaction flows this system is expected to be able to carry, written
from the *concept* of the system (a coordinator that facilitates work across team-owned execution
surfaces) and from general industry experience with ticketing systems, chat automation, review
lanes, on-call escalation and workflow engines. It is deliberately **not** a description of what the
current code does. The catalog is the yardstick: each scenario is later run against the system and
given a verdict, which produces the gap list.

**How to audit.** For each scenario fill in the `Audit:` line. Use the verdict vocabulary in
§ 16. Record the *observable* you checked, not your belief. Where a scenario's `Probe:` line names a
sharp edge, that edge is the interesting part — a scenario that "works" while its probe edge does
not is a partial, not a covered.

---

## 1. Vocabulary

Chosen so that no scenario depends on a product, a protocol, or a component name. When the
implemented system is discussed, its parts map onto these roles; the mapping is the implementation's
business, not the scenario's.

| Term | Meaning |
|---|---|
| **Request** | A piece of work someone wants done, expressed in the words the requester used. |
| **Work item** | The coordinator's durable record of a Request: its state, its history, its evidence, its owner. |
| **Coordinator** | The part that holds work items, decides who should do the work, records outcomes, and is allowed to speak into conversations. |
| **Executor** | The team-owned surface that actually does the work: a person, an automated worker, an AI worker, or a pipeline. |
| **Connector** | The team-owned bridge to one external system (ticketing system, code host, communication platform, pipeline service, directory). Holds that system's credential. |
| **Conversation** | Any place humans talk: a channel, a thread, a topic, a ticket's comment stream, a direct message. |
| **Channel** | One addressable place inside a communication platform, including its threads as sub-conversations. |
| **Ticket** | A record in a ticketing system. A ticket may host a conversation. |
| **Change request** | A proposed change to tracked content awaiting review (pull request, merge request, review request — all the same thing here). |
| **Participant** | A person or a non-human contributor whose activity is relevant to a work item. |
| **Non-human contributor** | An automated participant with a declared identity, a declared set of capabilities, and a place in a team's process. Not a person: it has no standing it did not bring, no authority beyond what its team granted, and no judgment the record treats as a decision. |
| **Owner** | The person accountable for a work item's outcome. Not necessarily the requester, not necessarily the executor. |
| **Approver** | A person entitled to consent to a specific action. Entitlement, not job title. |
| **Watcher** | A person or team that receives updates but has no standing to decide. |
| **Evidence** | What made the coordinator believe something: who said it, when, under which identity, and with which proof. |
| **Artifact** | A durable produced thing (report, patch, image, log bundle) that can be shown, linked, downloaded, and retained. |
| **Action** | A consequential step against someone else's system: write to a repository, deploy, spend money, delete, close, message a customer. |
| **Held action** | An action that will not happen until an Approver consents. |
| **Digest** | The stable fingerprint of exactly what was consented to, so a consent cannot be reused for a different action. |

Two words we avoid on purpose: *bot* and *integration*. Both hide decisions. Use "the executor
posted" or "the connector wrote", so it stays visible **whose** credential and **whose** authority
produced the visible result.

---

## 2. Axes

Every scenario is a point in this space. A gap in any cell is a hole in the product.

| Axis | Values |
|---|---|
| **Contributor kind** | a person · an automated worker · an AI worker · a pipeline · another team's contributor — each with a declared identity and a declared capability set, so responsibility is always traceable to a team |
| **Who initiates** | a person in a conversation · a person on a ticket · a person in an operator view · an API caller · another work item · a schedule · an external event (pipeline, alert) · an executor itself · a person who has no standing |
| **Conversation shape** | open channel · private channel · thread under a post · forum topic · ticket comment · direct message · a conversation the system has never seen · a conversation that is later renamed, moved, split, or archived |
| **Content of the ask** | plain sentence · mention with no context · command with arguments · attachment (image, log, document, recording) · forwarded content · quoted reply · a reply to the system's own message · the same content posted twice · content in another language |
| **Authority of the asker** | owner · team member · approver · linked stranger · unlinked person · service account · former member · external contractor |
| **What is at stake** | a read · a comment · a mutation of tracked content · a deploy or release · spending · a destructive act · a message to a customer · a change to policy or configuration |
| **Consent shape** | none needed · one approver · two approvers · not-the-requester · a role or group · a time limit · an expiry · a revocation · a re-consent after the thing changed |
| **Where the work runs** | a team's always-on surface · an on-demand surface · a surface that is offline · a surface on another team's infrastructure · a surface a person is watching live |
| **How it ends** | completed · completed with caveats · refused · failed · cancelled by a person · cancelled by policy · expired · superseded · handed to a person |
| **What a person is owed** | an acknowledgement · a visible decision-maker · progress · a place to answer a question · an outcome where they asked · a link to evidence · a reason, on failure |

---

## 3. Family A — work enters

The entry point determines what the person expects back, so this family is where trust is won or
lost first.

### A1 — Sentence in a channel, no mention
**Actors:** any member. **Trigger:** someone writes something that looks like work in a channel the
system listens to, without addressing it.
**Expected:** no new work item. Two defensible behaviours and no third: stay silent, or offer a
reaction/shortcut that converts the sentence into a Request. If it offers, the offer must expire
quietly.
**Probe:** does an unrelated statement in a busy channel create work? does every message get a reply
(notification storm)? is the difference between "listening" and "addressed" configurable per channel?
**Audit:** only explicit interactions trigger work or reactions. either mentioning the chatbot or adding a tag/category on a ticker, or stating the intent clearly will trigger any reaction. Normal conversations not involving any part of the facilitator go past it

### A2 — Explicit ask, open channel, one team
**Actors:** member, addressed. **Trigger:** `@system please summarise last week's build failures`.
**Expected:** an acknowledgement *in that channel* that names the work item and who will do it; a
later outcome posted in the same place; the person can find the work item again by link.
**Probe:** is the acknowledgement visible enough to distinguish "received" from "ignored", without
being noise? does a second identical ask from the same person make a second work item, or is it
merged with a pointer?
**Audit:** similar requests are deduplicated or merged (if there is enough merge conditions, then it probably becomes a steering request). Acknowledgement can be in two forms. A reaction for "smaller" requests, or a reply for larger requests (chat reply, comment reply in ticketing system, MR comment response, etc.). The facilitator can decide which type of response should it be.

### A3 — Ask with an attachment
**Actors:** member. **Trigger:** a screenshot or log file plus a sentence.
**Expected:** the attachment reaches the executor as something it can actually read, the work item's
history records that it was supplied, and the outcome can quote it back. If the attachment cannot be
delivered, that is stated, not swallowed.
**Probe:** is a *video* treated like a *document*? what happens to a 400 MB file, a 0-byte file, a
file the platform deletes after a day? does a forwarded copy of an already-seen attachment become a
second object?
**Audit:** let's only work with manageable attachment types (text, markdown, pdf, word, ppt, images) for now. We can expand on videos later if it seems reasonable.

### A4 — Ask on a ticket that tags the system
**Actors:** reporter (possibly outside the team), ticket. **Trigger:** a ticket is created or
commented and the system is named in it.
**Expected:** a work item linked to that ticket; the system collects context from the ticket
(description, comments, attachments, labels, who is involved) and starts work; progress and outcome
appear **as a comment on the ticket**, because that is where the reporter is.
**Probe:** is the first visible reply a receipt or a wall of text? does the system see a ticket edited
five minutes later? does it re-read a ticket when a developer adds the missing detail? if the ticket
belongs to a project the team has not claimed, does anything still run?
**Audit:** any action that should trigger the facilitator needs to be within the team scope. I imagine an outsider raising a ticket would result in facilitator tagging team members or reaching out via chat to them to notify them. When team approves/tags/indicates that the ticket should be worked on, then the work begins. Shorter replies are preferred (e.g. A reply to the external participant that "the team will look into it" so they get some feedback)

### A5 — Ask on a ticket, insufficient information
**Actors:** reporter, executor. **Trigger:** work starts and the executor needs a fact only the
reporter has.
**Expected:** the executor raises a question, the system posts it as a ticket comment addressed to a
person, the work item moves into a *waiting for someone* state that is visibly different from
*failed* and from *running*, the item stops burning capacity while waiting, and the answer — in
whatever form it arrives — resumes the same work item rather than starting a new one.
**Probe:** does the question name who must answer, or does it float unaddressed? what happens if the
answer arrives on the ticket and nobody mentioned the system? if the reporter answers in a chat
channel instead, is that the same work item? what if nobody ever answers — is there a stale
timer, and does it notify the Owner or the requester?
**Audit:** there should not be a specific dependency on any one person. The only blocker is the missing information. The provided information plus an explicit signal to resume should be enough. The 'Signal' can be a question @system, do you have enough information now? Or @system resume the work should be enough to trigger a resume

### A6 — Question raised into a chat channel instead of the ticket
**Actors:** executor, participants. **Trigger:** the work item's conversation lives on a ticket, but
the useful humans are in a channel.
**Expected:** a coherent rule for where a question goes, the answer is accepted from either place,
and every place that was told about the question receives the answer's effect.
**Probe:** is the same question asked twice in two places? do answers conflict, and if so, which one
wins and does anyone see that decision?
**Audit:** worst case scenario - the question is asked anywhere. Ideal scenario - the facilitator 
intelligence can choose the most appropriate communication channel to make sure that the question becomes visible

### A7 — Follow-up in the same conversation
**Actors:** any participant. **Trigger:** a further message in a thread that already has a work item,
no mention.
**Expected:** treated as additional input to the existing work item, not a new one; the history
shows it was taken.
**Probe:** at what point does a follow-up become a *new* request (new person asking, new topic, "and
while you're at it, also…")? does a participant who is not the Owner get to add input? does a
follow-up on a *completed* item reopen it, create a child, or get refused with a reason?
**Audit:** completed items should never be reopened. One "thread" - one "work item", anyone can communicate, 
but only the owner (or the team approved members) can act on actions. E.g. If teams have specific people
 that can trigger "work" these are the only people that can affect it, but everyone else can add context 
 and it should be accounted for (it's just steering).

### A8 — Ask through an operator view
**Actors:** owner, approver. **Trigger:** a person creates, edits or starts work from the
system's own interface.
**Expected:** full parity with chat and ticket entry; the resulting work item records the person as
requester with their real identity, not as "an operator".
**Probe:** is anything *only* creatable in one surface? can a work item created on a ticket be edited
in the operator view? does an operator see work items belonging to a team they are not in?
**Audit:** Operator view is not the "standard" interface that should be emphasized as a part of the core
workflows (even though it does exist). The answer is no - a single "work" can travel between multiple
communication/VCS/etc. integrations as long as the facilitator knows how to map it, and  keep some sense
of where the communication is active at the moment. There are preferred places though - e.g. code review should
usually be performed in a MR/PR, not in chat. Steering can be done anywhere, etc.

### A9 — Ask through an application interface
**Actors:** another system, on behalf of a person. **Trigger:** an API call creates work.
**Expected:** the call carries both the calling application and the person it acts for; the history
distinguishes "this application asked, on behalf of this person" from "this person asked".
**Probe:** can an application invent a person? can it act for a person who never granted it
anything? is there a rate or volume expectation per application?
**Audit:** NO! There should be no way to invent a person. A person must at at least one point be confirmed
e.g. an account in telegram, must be at least once confirmed to be linked to entity in my IDP. 
I expect some form of authentication request or other forms of "linking" the identitied across different systems
to be carried out for security purposes. Also, critical operations (as defined by the teaam) should always raise
an authentication/authorization procedre. E.g. person A says that they want us to deploy something to production =>
facilitator talks to the workspace to see what kind of auth is expected => facilitator responds with a "sure thing, just click on this
link to prove that you are who you say you are" => person A clicks on the link => the auth is performed via the integrated
IDP and a redirect is made towards the facilitator confirming the action. (this is just an example - it can look completely different
as long as it is secure)

### A10 — Work created by the clock
**Actors:** a team's schedule. **Trigger:** a recurring or one-off time trigger.
**Expected:** a work item with the schedule as its origin; a person is accountable for its outcome; a
repeated failure of the same scheduled item becomes visible instead of looping quietly.
**Probe:** who is the Owner of a scheduled item? does a schedule that has been failing for two weeks
say anything to anyone? do holiday/quiet-hours rules apply?
**Audit:** It should be brought up in any of the team visible channels. A chat message linking the owners, 
a ticket in the ISSUE tracker, etc. As long as some human is kept in the loop.

### A11 — Work created by an external event
**Actors:** pipeline, monitoring, deployment. **Trigger:** a build fails, an alert fires, a release
finishes.
**Expected:** the event becomes a work item with dedupe, so a flapping pipeline produces one item
that accumulates occurrences, not fifty.
**Probe:** what is the identity of "the same" failure? do related alerts group into one item with a
common cause, the way an incident does? can a person merge two items that clearly describe one
problem, and does the history survive the merge?
**Audit:** Partially, it can be human steered - "hey this ticket is a duplicate of ticket X". It can also utilize the facilitator 
intelligence that does this automatically when some treshold of certainty is achieved. History should never be squashed, it should be used as 
context.

### A12 — Work created by another work item
**Actors:** executor, a second team. **Trigger:** completing one item requires someone else's work.
**Expected:** a child item on the owning team with a visible link to the parent; the parent's history
shows the wait; if the child fails or stalls, the parent does not hang forever.
**Probe:** can a child block a parent across a team boundary the parents' approvers cannot see? what
happens to children when a parent is cancelled? can the chain be walked by a person auditing six
months later?
**Audit:** It should be an interrupting action. I imagine facilitator raising an issue in what ever chat interface.
e.g. "I need feature A to be implemented in the team B backlog to proceed, I will stop development now. Do you want me to notify the team?".
Notifying the team means reaching out via chat, creating a ticket with clear ownership and description and then also notifying the original team
that the ticket was created - the work ends there.

### A13 — Work someone else's team should own
**Actors:** person, two teams. **Trigger:** an ask arrives on the wrong team, or spans two.
**Expected:** either a routing decision is made from declared intent, or the item is placed for
triage with a human picking up the assignment. The person who asked is told who now owns it.
**Probe:** is misrouting silent? can a person hand an item to another team and keep watching it? does
the second team's private data become visible to the first just because a conversation mentions it?
**Audit:** any misrouting should raise clear alarm bells. There should be human in the loop to triage and decide.

### A14 — Ask that is not work
**Actors:** anyone. **Trigger:** a command that is nonsense, unsupported, or aimed at the wrong
system.
**Expected:** a short, specific refusal with the reason and what would have worked; nothing was
recorded as work; the failure is visible to whoever configured the channel.
**Probe:** is the refusal the same message for "I cannot parse this" and "I may not do this"? is a
refusal that repeats every few minutes from a retrying caller throttled?
**Audit:**Just a single refusal is good. As much information as possible, but never give out "secret" information like team ownership, other people's contacts, etc. Only what is "safe" to share.

### A15 — The same ask twice
**Actors:** one person, or two people, or a retry. **Trigger:** duplicated content arriving more than
once.
**Expected:** one work item, and the second delivery is acknowledged as a duplicate rather than
silently swallowed.
**Probe:** does dedupe look at content (too clever, blocks two genuine requests for the same thing)
or only at delivery identity (too literal, lets a person create five items by double-tapping)? is
there an escape hatch to force a second item?
**Audit:** if the uniqueness can be determined by the identity of the item itself, do it like this, otherwise use the intelligence.
E.g. Repeating the task "update translations" with different content is considered different tickets. saying the same thing with the same content is considered a duplicate task - regardless of the author.

---

## 4. Family B — people and standing

Industry experience is unambiguous here: most automation that gets switched off, gets switched off
because someone could not tell who was allowed to make it act, or because someone acted who should
not have.

### B1 — A person's identity is proven, not guessed
**Actors:** a new person. **Trigger:** they first interact with the system.
**Expected:** the person becomes a known participant only through a claim they can demonstrate
against their own identity system, at their own initiative. A display name, an avatar, a username or
an email never establishes identity.
**Probe:** is the linking step a deliberate act the person can complete unaided? can a link be
revoked? does an unlinked person get a usable message explaining how to link, and nothing else?
**Audit:** A person can start the link unatended. The person should also get a usable message on how to link. A link can be revoked.
Unknown person should never be allowed to participate in the interactions (or their commands should not be executed, however if that person is giving extra context, that is valuable information - e.g. an unknown person creates a comment on a MR - the comment should still carry weight - unless that comment is "deploy to production" for example)

### B2 — Unlinked person asks
**Actors:** unlinked person. **Trigger:** an ask from someone with no proven identity.
**Expected:** the ask is not executed as if it were trusted; the person is told the single next step;
the attempt is visible to a team member so it is not a dead end.
**Probe:** does an unlinked person's message still reach the executor as untrusted text? can they
watch, or only ask? if a ticket reporter is external to the organisation entirely, what is the
path?
**Audit:** The text still reaches the executor. They just cannot invoke actions from any part of the facilitator system.

### B3 — Person loses standing mid-flight
**Actors:** departed or moved person. **Trigger:** someone who asked or approved leaves the group.
**Expected:** already-recorded decisions do not evaporate; pending work does not become ownerless —
it surfaces to whoever is next accountable; a departed person's pending consent expires rather than
blocking forever.
**Probe:** is the Owner-less item visible to anyone? does an item owned by a service account that got
decommissioned become orphans?
**Audit:** It becomes owned by the team, never abandoned and never hidden.

### B4 — Direct message the system cannot open
**Actors:** a person, the system. **Trigger:** the outcome needs to reach someone privately, but no
private route exists yet (they have never opened a conversation).
**Expected:** an honest, specific failure that names the cause and the human fix, plus a fallback:
post in the shared conversation with no private detail leaked.
**Probe:** does a failed private message make the whole work item fail, or only the notification?
does the system retry, and how often?
**Audit:** An anonymized message is written into a public channel - an effort is given to allow the person to correct before it is abandoned.
When anything is abandoned, there should be clear and visible reason explaining why. The default mode is public communication. Only things allowed in private channels are auth related acts.

### B5 — Two people with the same display name
**Actors:** participants. **Trigger:** an instruction names "Alex".
**Expected:** the system resolves to a proven identity or asks which one; it never picks the first
match.
**Probe:** is a name ambiguity surfaced as a question or resolved silently?
**Audit:** If ambiguity exists, it is ALWAYS surfaced.

### B6 — Service accounts and machine people
**Actors:** another automation. **Trigger:** a pipeline posts a comment, a bot files a ticket.
**Expected:** machine-originated input is recognisable as such, so a human does not get paged for
something another machine said. Two automations must not be able to converse each other into an
infinite loop.
**Probe:** is there a loop budget? does the system recognise its own earlier message? when the
executor's identity is shared with a human's account (a "we all use the same token" setup), does the
history record that honestly?
**Audit:** Best case scenario - there should be a way to recognize it's own messages. Worst case scenario, there should be a loop budget.
Technically, that should not be a viable option, although a warning should exist telling whoever has the token to never share it.
Hope for the best, design for the worst.

### B7 — Delegation
**Actors:** an approver who is away. **Trigger:** their consent is needed and they have said who may
answer for them.
**Expected:** a delegate can decide, and the history shows both the delegation and the delegate.
**Probe:** is delegation explicit data or a rumour in a channel? can a delegate delegate further? do
delegations expire (holiday ends)?
**Audit:** A delegated identity needs to be explicit. It can also delegate further (basically a transfer of ownership)

### B8 — A person acts from a shared surface
**Actors:** a person at a kiosk, a shared laptop, a phone in a family plan. **Trigger:** they approve
something.
**Expected:** consent binds to the person's proven identity, not to the device — so a later dispute
can be settled.
**Probe:** is there any notion of "this device, not this person"?
**Audit:** No! The person should be able to properly authenticate themselves and not be tied to a device. Don't even consider this.

---

## 5. Family C — consent, refusal, and doing the consequential thing

The shape of an approval is what makes a system safe to leave switched on.

### C1 — Action with no consent needed
**Expected:** it runs, and the history says who asked, who was allowed, and where the rule lives.
**Probe:** can a reviewer reconstruct *why* this was allowed a year later?
**Audit:** As long as the data is kept, it should be reconstructible. If someone decides to keep a smaller autit window, that's their decision

### C2 — Action held for consent, single approver
**Actors:** approver, executor, coordinator. **Trigger:** an action crosses the team's threshold.
**Expected:** the work item stops in a state that says *held for approval*, and shows: what will
happen, to what, who asked, who is being asked, how long they have, and how to see the evidence.
The approver answers with a decision and optionally a reason. Once answered, the action runs exactly
once.
**Probe:** does the approver see enough to decide *in the place they are answering from*, or must
they open something else? does the description of the action say enough to make a rubber-stamp
impossible — for example the target and the amount, not just a category?
**Audit:** It is a completely viable solution that the person needs to navigate somewhere else to
make an informed decision

### C3 — Approver was not actually entitled
**Actors:** someone without standing. **Trigger:** they answer a held action.
**Expected:** a refusal that says why, is visible to entitled approvers, and does not consume the
held action.
**Probe:** does a wrong answer burn the opportunity? can an unentitled person see the question at
all, and does that leak anything?
**Audit:** They can see the action - they cannot act upon it. The principle of this entire system is
transparency, but keep in mind the level of transparency (no exposing secret information)

### C4 — Two people answer at once
**Expected:** one decision wins; the other is told, truthfully, that the item was already decided by
someone else, and nothing half-happened.
**Probe:** does the loser's reason or comment survive anywhere?
**Audit:** sure, but it is not actionable - it survives because communication is done in external applications anyway

### C5 — Consent goes stale
**Actors:** approver, executor. **Trigger:** the thing being consented to changes after consent was
given, or time passes, or someone withdraws it.
**Expected:** consent is bound to exactly what was shown (a digest of the proposal), so a change
forces a new consent; expiry is a real terminal state that cannot be revived by a slow approver;
withdrawal before execution always wins.
**Probe:** if the executor's plan changed shape (bigger change, different target, second system
involved) does the earlier consent still let it through? is expiry enforced on read as well as on
write? does a person who approves at 09:00 a proposal that was withdrawn at 08:59 get told, politely?
**Audit:** It does not flow through. Approval is tied to a digest, I aggree!

### C6 — Consent needed, nobody answers
**Expected:** the held action times out into a visible terminal outcome, the person who asked is
told, the Owner is told, and the item is not left *running* forever.
**Probe:** is there a nudge before the deadline, and to whom? does a timeout retry the whole ask or
drop it? is a repeated timeout by the same team visible as a pattern?
**Audit:** A nudge is a good idea. A timeout can probably restart the work from a branch from coding related things and with the context already received from the workspace, restarts should only be done explicitly - if asked by humans.

### C7 — Consent from the conversation where the work started
**Actors:** approver in a channel or on a ticket. **Trigger:** the held action is announced in the
same conversation as the ask.
**Expected:** a person can decide from where they already are; the announcement names the decision
and enough of the proposal to decide; the identity behind a chat-borne decision is as strong as a
decision made on the operator's own screen.
**Probe:** does a chat-borne decision carry group or role membership, or only a personally named
approver? does the announcement actually arrive in that conversation, or is chat a decision surface
nobody can discover?
**Audit:** A good approach would be that a decision in chat manifests in a ticket in the issue tracker and then a seperate "work" carries that ticket. Alternatively, research purposes don't need tickets and can survive chat only instruction (if scoped by size). The natural flow should always be to have some sort of a ticket stored somewhere - if nothing else, for auditing purposes. The operator should also be able to see who triggered what and where, so this is a firefighter option as well.

### C8 — Consent for a batch
**Actors:** approver, many items. **Trigger:** six routine items await the same kind of consent.
**Expected:** an approver can see them together and answer together, while the history still records
each decision against each item.
**Probe:** is batching a shortcut that weakens what is shown (six items, one line of
description)? does one refusal in a batch cancel all six?
**Audit:** No. One approval for one item only! Never batch, it's bad for auditability. Even if 6 items are approved, they should be treated as 6 different "works"

### C9 — Same person asked and answered
**Expected:** a team's rule can require that the one who asked is not the one who consents, and the
system enforces it rather than trusting good manners.
**Probe:** when work was initiated by an automation, is "the requester" the automation or the human
behind it? is there a break-glass route with a recorded reason?
**Audit:** Team owners can always interject. Any significant work should always be carried by a human. E.g. A research work can be carried by the system if the result is a jira ticket. A jira ticket should have a human owner. 

### C10 — Consent for a change to the rules themselves
**Actors:** team owner, operator. **Trigger:** someone edits what is allowed, who is entitled, or
which team owns what.
**Expected:** privileged changes are made by named people, recorded with a reason, and take effect in
a way an auditor can follow. A change that would silently widen authority can be made to require a
second person.
**Probe:** is a policy edit visible in the same trail as the actions it enabled? can a policy change
retroactively legitimise something already done?
**Audit:** Changing the rules and gauges of the system can only be done from the admin UI and only by the ones having an operator role. Period.

### C11 — Refusal by policy
**Actors:** team rule. **Trigger:** an action nobody permitted.
**Expected:** a clear, non-technical refusal, with the rule's home named, and the requester told
where to ask for an exception. Silence is the worst outcome because it looks like a bug.
**Probe:** does a refusal look the same as a failure? is "you could ask your team owner to allow
this" actionable?
**Audit:** It should carry the description on what the next actions should be. Not exactly like failure, but the intent should be clear "I cannot do this because ...."

### C12 — Emergency route
**Actors:** on-call person. **Trigger:** an incident where the normal consent path is too slow.
**Expected:** a faster route exists, is narrower than normal authority, is recorded with a reason and
a person, and produces an after-the-fact reviewable trail.
**Probe:** is the emergency route discoverable under pressure? is it obviously a route a human took?
does anything about it expire?
**Audit:** I don't know how to answer this. I haven't considered it as a part of the original system.

### C13 — Consent to talk to a person outside the team
**Actors:** requester, external customer. **Trigger:** the executor wants to send a message that a
customer will read.
**Expected:** sending outward is itself a consent-worthy act for some teams; a draft is shown before
it goes.
**Probe:** is outward communication distinguishable from internal chatter in the history? can a
team turn this consent on per channel?
**Audit:** The system should not support a scope like this today! The facilitator should be developer/business person facing only - internal to the company

---

## 6. Family D — keeping people informed while work is running

### D1 — Progress that earns its notification
**Expected:** a person watching learns the item is moving, without a message per step. The default
is a small number of meaningful updates; detail is available on demand.
**Probe:** is progress grouped, throttled, or per-event spam? can a watcher choose their level?
**Audit:** The team owner can choose chattiness, team member can request a one time applicable "update" or "status update".

### D2 — Nothing seems to be happening
**Actors:** watcher, executor. **Trigger:** work is quiet for longer than expected.
**Expected:** the item is visibly *not stuck* or visibly *stuck*, and if stuck, someone is told.
**Probe:** is a quiet-on-the-network worker distinguishable from one that died? does the system ever
say "I gave up" after "I am working on it"?
**Audit:** There should be caps in place. Both for team level scaling (handled by the gateway) and for task level scaling - handled internally by the team harness. Nothing regarding a single task caps should be in the scope of facilitator other than understanding the "limit reached" or similar messages from the harness or the gateway and delivering the "notification" to the team.

### D3 — Work moves to a different executor
**Expected:** a person sees that the work moved and to which team, and the reason; a hand-over does
not look like a fresh start.
**Probe:** does the item's history survive a move? does a person's subscription follow it?
**Audit:** No. This functionality is not in scope (yet). When it is, I assume the history should survive the move, person's subscription should not.

### D4 — Partial success
**Expected:** "two of three things done, third needs your input" is expressible. A person is never
told "done" about something only half done.
**Probe:** does the outcome state carry the caveat, or is the caveat only in prose?
**Audit:** Caveat should be dealt with before the "work" is done. Definition of done could also say that a new ticket was put in the backlog. It needs human interaction.

### D5 — Where the outcome lands
**Expected:** the outcome appears where the request came from, plus a durable link; a person who saw
the request can find the result without searching.
**Probe:** does an outcome posted to a ticket also reach the channel it started in, and is that
duplication or a courtesy? does an outcome reach the original *person* if they left the channel?
**Audit:** If ticket is the originator and the conversation continues in the chat, it is courtesy to notify the chat as well as the ticket. Final confirmation of done should be in the ticket (the origin).

### D6 — The system speaks as itself
**Expected:** anything the system posts is unambiguously from the system, never dressed as a human,
and it says which team's authority it used.
**Probe:** can a reader tell whether the executor or a person wrote a comment? does the system ever
post using a human's credential, making it look like that human said it?
**Audit:** NEVER! Everything the system does should be clearly tracable to the system. The system can link the human post though...

### D7 — Silence as a feature
**Expected:** per-channel, per-team, and per-person quiet rules: some teams want receipts, some
want only outcomes, some only failures.
**Probe:** is quiet-hours or per-channel muting a first-class setting? does muting suppress only
notifications, or the record too?
**Audit:** There is no such thing planned for this version. If it were, it would be in the operator/team owner domain

### D8 — Incident tempo
**Actors:** on-call, several teams. **Trigger:** one event causing many items at once.
**Expected:** one coordinated view with a shared conversation, one Owner, updates merged; a person
can find the single place where the situation is being discussed.
**Probe:** does the system create 40 items and 40 approvals for one incident? is there a pause
button for the whole situation?
**Audit:** Items are tracked by an issue tracker. This kind of complexity is not planned in the current version - maybe the next iteration.
My feeling would be that a single incident would create a single item. I might be wrong though.

---

## 7. Family E — outcomes and handover

### E1 — Clean completion
**Expected:** the state says so, evidence and produced things are attached, the requester was told in
their place, the conversation shows what changed, and the item can be reopened or referred to later.
**Probe:** is "completed" a claim or is it backed by something checkable?
**Audit:** I needs to be backed by something checkable. Usually it will be an open MR, sometimes a document or a new issue with the research or refinement. Sometimes it will be a new application version in production as a result of work. Just saying that 
it is complete is not enough.

### E2 — Produced things shown in place
**Actors:** requester. **Trigger:** the work produced a file, an image, a patch, a recording.
**Expected:** produced things are viewable where the outcome is reported, with an honest note about
who may read them; large ones are summarised, not dropped.
**Probe:** does a produced thing outlive the conversation that requested it? does a viewer who is not
on the producing team get a clear "not visible to you" rather than a broken image?
**Audit:** ofcourse the artifacts need to outlive the conversation. What is the point otherwise?!

### E3 — Work handed to a person
**Expected:** the system can stop being the executor and leave a human todo, keeping the history, the
context, and the eventual closing of the loop.
**Probe:** can the person report the outcome back into the same item? does the system notice the
human took over and stop pushing?
**Audit:** It should. And it should stop. Either the human let's the system know, closes a ticket, or some other terminal action (PR gets closed, command for /stop gets commented or something like that).

### E4 — Completion someone should check
**Actors:** reviewer. **Trigger:** the work produced a change to tracked content.
**Expected:** a review lane appears where reviewers already are, review requests go to the right
people with the right context, and the review's outcome drives the work item forward.
**Probe:** does the system know which review is for which item? does it notice an approval from
someone with no standing there? does it re-ask a reviewer who already said no?
**Audit:** It could go both ways - either the work is done and a new work starts with old work's context and MR comments, or existing work picks up. I would much prefer this to be seperate work - there can be a long pause between creating a MR and getting it reviewed - we don't want to wait for it to move into reviewed state. I guess the teams should know how to tailor their workflows and facilitator should handle them in a granular enough level so most of the workflows can be fit into it.

### E5 — The requester disagrees with the outcome
**Actors:** requester, owner. **Trigger:** work completed and the answer is wrong.
**Expected:** the requester can say so; that becomes input to a follow-up or a reopened item rather
than a new unrelated item; nothing is lost.
**Probe:** is there a difference between "wrong" and "I changed my mind" in the flow? can an outcome
be withdrawn or corrected after the fact?
**Audit:** There should be no difference and the flow should be handled the same way as for example the code review flow.

### E6 — Nothing was produced
**Expected:** a refusal with a reason is a legitimate outcome, distinct from a failure, distinct from
an expiry.
**Probe:** can a person tell these three apart a week later from the record alone?
**Audit:** They should. Either a reason for closing the ticket or a comment in chat should suffice.

---

## 8. Family F — failure, interruption, recovery

A system is judged not by its successes but by how it looks when things go wrong.

### F1 — The place doing the work is offline
**Expected:** work waits with a reason a person can read; queued work is visible as queued; when the
surface returns, work resumes without a new request; nothing is silently dropped on the floor.
**Probe:** how long before a queue becomes somebody's problem? does a queue that never drains get
louder?
**Audit:** It should surface the issues to system operators - the ones that can do something about it.

### F2 — Interruption mid-execution
**Expected:** an interrupted attempt either resumes or is reported as an attempt; the outcome of the
previous attempt is never double-applied where that matters.
**Probe:** is the retry visible? does a retry of a partially applied action duplicate a side effect
(post, comment, deployment)?
**Audit:** Everything that was recorded in a previous attempt should become context to the next attempt. Never repeat the work unless absolutely necessary.

### F3 — Repeated failure of the same thing
**Expected:** retries are bounded, the attempt count is visible, and after the bound a person decides.
**Probe:** is there an owner for a repeatedly failing item? does it stop by itself?
**Audit:** This should not be something that facilitator needs to handle. There should be a human in the loop at least at 
the task outcome - the human needs to be smart enough to decide that

### F4 — Work is cancelled
**Actors:** requester, owner, operator. **Trigger:** someone decides it should not finish.
**Expected:** cancellation is honoured everywhere it matters — including a held action — and the item
closes as cancelled by a named person. Anything already done is stated.
**Probe:** can a person who merely asked cancel? does cancellation reach a worker that is offline, or
only a running one? if part of the work already had visible effects, is that disclosed?
**Audit:** if the effects are irreversible (e.g. A deployment) that needs to surface as a critical thing. If the effects can be reversed (commits to isolated branch), then a mention is good enough and not even necessary.

### F5 — The conversation goes away
**Trigger:** a channel is renamed, moved, split, archived, or the ticket is closed.
**Expected:** the work item survives; delivery to a place that no longer exists degrades to a stated
fallback; the history still links to the old place.
**Probe:** is an item orphaned by a rename? does closing a ticket destroy the trail?
**Audit:** The context should remain in the facilitator as long as audit logs are kept. We should not go looking for changes in integrations, just deal with the consequences - mark it as obsolete channel and move on.

### F6 — External system is unhappy
**Trigger:** a ticketing system, code host or platform refuses, rate-limits, or is briefly unreachable.
**Expected:** the system backs off, says so, and does not lose its place. The difference between
"temporarily unavailable" and "not allowed" is visible to whoever can fix it.
**Probe:** does a rate limit stall one team or everybody? is a permission problem reported to a
person who can grant it?
**Audit:** pesmission problems are always disclosed to the team operators (the ones that can fix it). A rate limit stalls the current integration, not the whole system. The issue is disclosed to system operators.

### F7 — The system loses its own state
**Trigger:** a coordinator outage, a restart, a failover.
**Expected:** nothing a human decided is lost, no consent is applied twice, no item is forgotten,
people see a gap rather than a lie.
**Probe:** are held actions still held after a restart? does an approver get asked twice?
**Audit:** ideally everything should be held after restart. There is no major issue if some things get repeated - as long as they can safely be re-delivered it should be fine.

### F8 — A person cannot tell what is happening
**Actors:** a new team member. **Trigger:** they look at an item mid-flight.
**Expected:** one readable account: what was asked, by whom, what has happened, what is waiting and
for whom, what happens next.
**Probe:** is the "waiting for X" line a person or a machine? is the account the same shape as the
history, or a second story?
**Audit:** history is kept by the integrating system. We don't need to care. Not our problem

---

## 9. Family G — several teams

### G1 — One system, many teams, visibly separate
**Expected:** each team sees its own work, its own rules, its own private produced things; the shared
parts are only shared deliberately.
**Probe:** does a global list leak other teams' items by existing? is a private channel's content
ever quoted into another team's view?
**Audit:** Never. Work does not mix. Teams can know about eachother and other teams public knowledge and memories, that's it.

### G2 — Handover between teams
**Expected:** the receiving team gets what it needs and nothing more; the handing team keeps enough
context to answer questions; a person on either side can see the seam.
**Probe:** is the seam an explicit object with consent, or a copy-paste of a message? does a
handover carry produced things, and does their access rule change at the seam?
**Audit:** This complexity was not planned at this point. Let's think about it later.

### G3 — Two teams must both agree
**Actors:** two approvers, two teams. **Trigger:** a joint action.
**Expected:** both consents are needed, neither is visible in a way that lets one team read the
other's private material, and if one refuses the other is told the outcome without being shown
private reasons.
**Probe:** is joint consent expressible at all? does a partial consent sit somewhere findable?
**Audit:** This complexity was not planned at this point. Let's think about it later.

### G4 — A team's rule says no to another team's request
**Expected:** the requesting team learns the answer without learning the other team's internal rule
details beyond what is needed.
**Probe:** does a refusal leak policy?
**Audit:** This complexity was not planned at this point. Let's think about it later. - humans should handle this

### G5 — One person in several teams
**Expected:** their standing differs per team and the system says so; their inbox of held actions is
per-team and attributable.
**Probe:** is an approval request understandable without knowing which team is asking?
**Audit:** it should be evident from the context of the communication for which team the person is speaking on behalf of

### G6 — One team's automation on another team's content
**Actors:** team A's executor, team B's repository. **Trigger:** cross-team work.
**Expected:** whose credential is used is explicit; whose rules decide is explicit; those two can
differ, and when they do, it is recorded.
**Probe:** does a cross-team action fall back to a superuser credential nobody owns?
**Audit:** This complexity was not planned at this point. Let's think about it later.

### G7 — Shared channel, competing claims
**Actors:** two teams in one channel. **Trigger:** an ask with no team stated.
**Expected:** either routing is decided from declared intent, or the ambiguity is asked. Two teams
must not both start work on one sentence.
**Probe:** is a race detected, or does it look like a duplicate item?
**Audit:** never duplicate work. Always clarify intent before starting work. If it's not clear, express it, ask the question.

### G8 — Team topology changes
**Trigger:** a team is renamed, merged, split, or disbanded.
**Expected:** work items keep their meaning, approvals and rules follow the successor, and historic
records stay readable.
**Probe:** does a disbanded team's queue have an owner? does the trail survive a rename of the owner?
**Audit:** This complexity was not planned at this point. Let's think about it later.

---

## 10. Family H — knowledge, evidence, and being asked again later

### H1 — Context is reused, and its limits are honest
**Expected:** the system may use what it already knows about a project, but presents remembered
knowledge as context rather than authority, and shows where a belief came from.
**Probe:** can a stale belief be corrected, and does the correction take effect where the wrong belief
was shown? does remembered content from a private place leak into a wider one?
**Audit:** context can be either team owned or system wide. The context/memories should be self healing and self correcting. Private memory can only leak into public one if it is re-categorized or shared directly in a public integration (e.g. A ticket in another teams issue tracker)

### H2 — Asking the same question later
**Actors:** a newcomer. **Trigger:** "why did we do X six weeks ago?"
**Expected:** the decision, the person, the date and the produced thing are findable from the item.
**Probe:** is the answer a searchable record or archaeology?
**Audit:** yes as long as the histroy was not pruned. Otherwise the response is just "I don't know" or a record found in the
existing documentation and facts (outside of the system).

### H3 — Audit request
**Actors:** auditor, security. **Trigger:** "show me everything the automation did to our system last
quarter, and who allowed it."
**Expected:** one answer, per action, with the decision behind it and the identity that made it,
exportable, with no raw credentials in it.
**Probe:** does the trail contain both allowed and refused actions? is a policy edit at the time
visible? can a produced thing be tied to the person who released it?
**Audit:** yes, the scope should include what ever the operator wants and is available in the system.

### H4 — Sensitive material
**Actors:** team owner, compliance. **Trigger:** an attachment or message contains something
restricted.
**Expected:** a team can decide what may be stored, for how long, and who may read it; the system can
forget on request.
**Probe:** is forgetting real (produced things too)? does an attachment quoted into a summary escape
its own rule?
**Audit:** forgetting is real and not escapable. This complexity was not planned at this point. Let's think about it later.

### H5 — Retention and handover at the end
**Actors:** team, operator. **Trigger:** work ages out.
**Expected:** a stated lifecycle for items, produced things and histories, with a rule for items that
must be kept longer than usual.
**Probe:** is deletion of an old item distinguishable from "never existed"?
**Audit:** I am not certain. The result is the same regardless - "I don't know"

---

## 11. Family I — operating the system as a person, not an infra engineer

### I1 — A team turns it on
**Actors:** team owner. **Trigger:** first day.
**Expected:** connect one ticketing system, one communication space, one place to work; the team
chooses which asks the system acts on, which need consent, and where it may speak. A team can be
useful in an hour.
**Probe:** is there anything to click, or only files to write? what is the smallest useful
configuration?
**Audit:** For my usecase (the one I am exploring) the minimal surface is a communications platform (of any kind) and a workspace (harness). Everything else is just candy on top. Somewhere to communicate bidirectionally with the harness.

### I2 — Turning down one behaviour
**Actors:** an irritated team. **Trigger:** an automation is unhelpful.
**Expected:** a narrow switch exists. Teams do not disable whole systems to stop one behaviour.
**Probe:** is the only mute button a global off? is the switch per channel, per team, per rule?
**Audit:** Switches usually work on all three levels, but on the team level, only the operators can turn them. On the system level, the system operator can turn the switches and they override any crosscutting concerns over the team preferences.

### I3 — Someone asks "why did it do that?"
**Actors:** any participant. **Trigger:** a surprising action.
**Expected:** the item explains its own cause: the ask, the routing, the rule's home, the consent
that allowed it, who was involved.
**Probe:** can a non-operator get that answer? is it a link to a log or a sentence?
**Audit:** A team member can get a summary, of course. An operator can get the full details, the audit logs, everything recorded in the system. A team member in chat gives a brief description, a pointer to the place where a decision was made and limited context.

### I4 — A change to a team's rules
**Actors:** team owner. **Trigger:** the team wants one more action allowed.
**Expected:** the change is a small deliberate act, testable before it is live, and reviewable after.
**Probe:** does widening authority require an infra change? can a team try a rule without enabling it
for real?
**Audit:** it could be an infra change if it is new integration involved. It could be a configuration change if it's related to behavior or auth. Operators can do that, teams should not be able to experiment on their own.

### I5 — Capacity and fairness
**Actors:** a team with more asks than capacity. **Trigger:** load.
**Expected:** visible queueing, a notion of priority, a cap on concurrent automation so one
enthusiastic project cannot starve the rest.
**Probe:** is priority a real field or just arrival order? is starvation visible?
**Audit:** Arrival order. Starvation is visible, scaling laws for team operated environments live in the team environments (e.g. The gateway controls the scale of harnesses allowed, the team has their own LLM token budgets and they control the environment budgets)

### I6 — Cost is visible
**Actors:** owner. **Trigger:** work consumes something chargeable.
**Expected:** the person who asked can be told what it cost, and a team can cap it.
**Probe:** does cost attach to an item, a team, or a conversation?
**Audit:** Cost (tokens spent, etc.) attaches to "work".

### I7 — The system is being replaced or a connector is withdrawn
**Actors:** operator. **Trigger:** a team leaves, a system is decommissioned.
**Expected:** history stays readable without the connector; items do not become lies.
**Probe:** is history readable when the source that produced it is gone?
**Audit:** As long as the history contains useful context, it should remain available.

---

## 12. Family K — a non-human contributor inside the team's process

These scenarios ask one question: when an automated participant takes part in a team's ordinary
process, does it do so **as a declared, bounded, traceable member of that team's process**, or as an
outside script that happens to write into it? Nothing here assumes the contributor is anything like a
person; what it assumes is that the team's process has a slot for it, and the slot is explicit.

### K1 — A contributor with a name the team can point at
**Actors:** team owner, any participant. **Trigger:** an automated participant first takes part in a
team's work.
**Expected:** it has a stable identity the team created, a stated capability set, a stated owner (a
person or role accountable for its behaviour), and a stated home team. Other participants can look it
up the way they would look up a colleague's entry: what is this, whose is it, what may it do.
**Probe:** does it appear in the same directory/list a person is looked up in, or is it a magic
string in a config file? can a new team member discover its capabilities without reading
infrastructure? is the accountable person recorded or assumed?
**Audit:** depending on the integration platform, it can be a bot, a "synthetic account" or anything else that is supported by the platform. 

### K2 — A contributor takes an ordinary task from the team's queue
**Actors:** contributor, team. **Trigger:** a task of a kind the team has agreed it handles is
assigned to it.
**Expected:** it is a legitimate assignee in the team's normal flow — it appears in who-is-what lists,
holds the task, moves it through the team's states, and hands it on or reports it. Being automated
changes the mechanics, not the accounting.
**Probe:** can a task be assigned to it from the same surface a person is assigned from? does its
work count in the team's throughput and load figures, or does it produce a shadow workload invisible
to planning? if it stalls, does it show up in the team's normal stalled view?
**Audit:** it's a seperate identity. The team knows it's a bot. It has a name and a unique id. the KPIs or any
other tracking is not in the scope currently.

### K3 — It contributes where the team's process is actually followed
**Actors:** contributor, reviewers. **Trigger:** the team's lifecycle runs on tracked content —
branching, change requests, review, checks, merges — and the contributor produces a change.
**Expected:** its contribution enters that lifecycle in its ordinary shape: a change proposal of the
team's usual kind, targeted at the right reviewers, carrying the context a reviewer needs, responding
to review comments, and waiting for the same gates everyone else waits for. It does not bypass a
review stage because it is fast, and it does not get a private channel of one.
**Probe:** does a change request authored by it look like everyone else's in structure (description,
tests, linked work item, review requests), or like a machine dump? does it get the team's default
reviewers or an empty review lane? do the team's required checks apply to it, and can it be made to
fail them like anyone's?
**Audit:** It should look natural - intended for humans to read, have enought relevant context. The reviewers should be the team members - the integration (adapter) can decide on a group or individuals who need to be included in the review. Every check that applies to the team needs to apply to this contributor. There should be zero difference.

### K4 — Review of its output, by whoever the rules name
**Actors:** reviewer, contributor. **Trigger:** a human reviews what the contributor produced.
**Expected:** review of automated work is normal review with one addition — the reviewer can see
enough of how the result was produced to judge it (what was changed, what was assumed, what was not
attempted). The contributor answers factual follow-up about its own output; disagreement ends at a
human's decision.
**Probe:** is the reviewer told which of the change is machine-generated? is there a limit on what the
contributor may answer to ("I did not check X") rather than a confident blank? does the system record
that a human, not the contributor, accepted the objection or the fix?
**Audit:** The integration alone should keep ample audit traces to know who made the decision. the automation
should respond to any objections, work on improvements, and participate in the code review as any other participant.
Facilitator does not need to track any content it does not already track - the discussions on the MR inside the VCS should
be enough to decypher the ownership and the discussions leading up to a change

### K5 — Following the team's written conventions
**Actors:** contributor, team. **Trigger:** the team has conventions — branch and commit shape,
comment style, label discipline, ticket workflow, ownership trailers, who must be told about what.
**Expected:** the contributor conforms, and conformance is treated as part of the task's definition of
done rather than as a nicety. Where it cannot conform, it says which convention it skipped and why.
**Probe:** are conventions configuration the contributor reads, or prose a human repeats to it? does a
missed convention surface as a reviewable defect, or as drift nobody notices? can a team add a
convention without changing the contributor's code?
**Audit:** Conventions are hardcoded in the harness and owned by the team. The team is responsible for instructing,
keeping documentation, skills and other tooling needed. This is not something that the facilitator needs to
be aware of. It is owned by the team.

### K6 — Its identity is used honestly
**Actors:** contributor, auditors, other participants. **Trigger:** the contributor writes into
someone else's system.
**Expected:** its writing is attributable to itself, not to a human whose credential happened to be
at hand; if a team chooses to write under a shared account, the record says so plainly and the
consequence (a human looks like the author) is visible rather than buried.
**Probe:** does any visible artefact credit a human with something a machine did? can the team
distinguish "the contributor did this" from "the contributor did this while acting for this named
person"? is that distinction auditable a year later?
**Audit:** never credit a human. There should be clarity on who is contributing. It is up to the team to properly set 
any identity needed to distinguish it from the rest of the team. The contributions (e.g. The MR description, the commit message, etc.) should contain a signature (something clearly stating - this was built by AI)

### K7 — A contributor's authority changes with the team, not with the weather
**Actors:** team owner, contributor. **Trigger:** the team's process matures — it starts letting the
contributor do more, or retracts something.
**Expected:** an explicit, reviewable change to the contributor's remit: what it may act on, where it
may speak, what needs consent, what is out of bounds. The change has a date, an author, and a reason,
and it does not require rewiring infrastructure.
**Probe:** can a team widen or narrow a contributor's remit without an operator? is the current remit
readable by an ordinary team member? does anything prompt a periodic look at what was once granted?
**Audit:** only an operator can do that. Team operator is a part of the team, so it should be close by.

### K8 — It reports facts about its own limits
**Actors:** contributor, owner. **Trigger:** a task exceeds what the contributor can actually do with
the access or information it has.
**Expected:** it says so early and specifically — what it could not reach or verify, and what a human
must do — rather than producing a confident partial result or stopping silently. Its self-report is
part of the record, and is trusted only as much as any unverified claim.
**Probe:** is "I could not run the tests" reachable as an outcome, or does the system only model
success and crash? does the partial result carry its caveat into the outcome state, or only into
prose? does anyone get told that the gap needs a human?
**Audit:** This needs to be clearly stated. I still believe that this part is something that the team owned
harness setup needs to handle. I don't think we need any funtionality planned in the facilitator for that

### K9 — Several automated participants on one team
**Actors:** two or more contributors, team. **Trigger:** a team has more than one automated participant
doing different parts of its lifecycle.
**Expected:** each has its own identity and remit, the team can tell them apart in its own records,
and their work does not collide: two contributors touching the same thing is detected as a conflict,
not discovered as a surprise. Their traffic is budgeted so a team's shared spaces stay readable.
**Probe:** is a shared catch-all identity used for everything, making attribution meaningless? is a
contributor-versus-contributor collision detectable before merge? is per-contributor volume capped?
**Audit:** The contributor can scale based on team allowances. I assume the identity will be a single identity
shared by multiple instances. Technically, the team can roll two instances of the same integration and use 
different identities. This is up to the team to handle. The facilitator needs no extra functionality for that.

### K10 — Onboarding and offboarding a contributor
**Actors:** team owner. **Trigger:** the team retires a contributor, or replaces it with a different
one doing the same job.
**Expected:** retiring is a deliberate act with an effect on open work (its tasks go somewhere) and a
readable afterlife (its history stays legible with its identity intact). Replacing it does not rewrite
who did what.
**Probe:** do its held actions and open tasks follow a defined retirement path? does a retired
contributor's past work become ambiguous once its identity is gone?
**Audit:** Yes. It's treated the same way as if a team member would leave the team. Let the team handle it.

---

## 13. Family L — the contributor as part of a company's working practice

Where K is about one team's process, L is about the wider company: the contributor's activity has to
sit inside rules written for people, for good reason, without pretending to be a person.

### L1 — Its work is inside the company's rules, not beside them
**Actors:** compliance, security, team. **Trigger:** company policy governs who may change what, how
releases happen, what needs sign-off, what must be logged.
**Expected:** an automated participant is subject to those rules through its owning team — same
sign-off gates, same records, same retention, same review cadence. There is no class of company action
that only humans are governed by because a machine was involved.
**Probe:** does an automated action appear in the same compliance reports a human action appears in?
does any rule silently apply only to people, leaving automated work ungoverned or over-governed?
**Audit:** This complexity was not planned at this point. Let's think about it later.

### L2 — Accountability is a person, always
**Actors:** team owner, auditor. **Trigger:** an automated participant's action causes harm — a bad
release, a lost change, an outward message a customer did not deserve.
**Expected:** the trail answers, in order: which contributor acted, under which grant, on whose work
item, in whose team, with which human's consent, and who owns that contributor. A machine is never the
accountable party; the accountable party is never a mystery.
**Probe:** can the chain be produced without a person's help? does a shared team credential anywhere
break the chain? if consent came from a group rather than a person, is an individual still
identifiable?
**Audit:** The group is responsible. The person steering the process is identifyable. Let's not go into more details.

### L3 — Company-wide visibility, team-level privacy
**Actors:** management, several teams. **Trigger:** the company wants a view of what automated
participants are doing across teams — volume, kinds of action, failure rates, spend.
**Expected:** aggregate visibility exists without forcing any team's private content into the open,
and it is the same visibility a team gets about its own contributors.
**Probe:** can the company count automated actions per team without reading the actions' contents? is
a team able to see how it compares? are blind spots (a contributor nobody can account for) visible as
blind spots?
**Audit:** NEVER! Team autonomy over everything else.

### L4 — A contributor's remit reflects role, not ubiquity
**Actors:** team owner, security. **Trigger:** a contributor present in many systems needs least
privilege — enough to do its task, nothing else.
**Expected:** each contribution is made with the narrowest standing that completes it, and that
standing is stated per capability rather than once for the whole contributor. A contributor that only
reads tickets does not carry a credential that can deploy.
**Probe:** is there a single powerful credential behind many behaviours? can two capabilities of one
contributor have different standing? is escalation possible with a record (read → write, just this
once)?
**Audit:** No, there should be a clear granularity of what types of access the credentials provide. This should be 
team owned. Nothing needs to be done in the facilitator.

### L5 — Behaviour the company would not want inherited by the next contributor
**Actors:** team owner, operator. **Trigger:** the company standardises practice across teams using
several automated participants.
**Expected:** a team's decisions about what is acceptable automated behaviour are expressible and
comparable — so a good arrangement can be adopted elsewhere and a bad one is visible as an outlier,
rather than each team having an undocumented arrangement.
**Probe:** is a team's arrangement inspectable as data, or tribal knowledge? is there a shape of
"standard team arrangement" a new team can start from? can an unusual grant be found by search rather
than by interview?
**Audit:** It is always recorded. One example is that the team owns the harness (and it's setup), and the harness is 
source controlled. There is no predefined shape.

---

## 14. Family M — things that must *not* happen

Negative scenarios are the fastest way to find gaps, because an implementation that never considered
them will answer these with silence or with a surprise. The **Family** column points at the positive
family the failure belongs to, so a partially audited family can be swept for its negatives without
reading the whole table.

| ID | Must not happen | Family |
|---|---|---|
| N1 | An unrelated conversation creates work, or every message gets a reply. | A |
| N2 | The system answers its own message and starts an unbounded exchange with another automation. | B |
| N3 | An action runs that nobody permitted, or is permitted by a rule nobody can point at. | C |
| N4 | A person's display name is treated as their identity, or a name collision silently picks someone. | B |
| N5 | Consent to one thing is accepted as consent to a different thing that merely looks similar. | C |
| N6 | An expired or withdrawn consent executes anyway because a slow process got there later. | C |
| N7 | A failure looks like a refusal, a refusal looks like silence, or silence looks like completion. | F |
| N8 | An outcome is posted where nobody was told the request existed. | D |
| N9 | Private material from one team becomes visible to another team by being quoted into a shared conversation. | G |
| N10 | A produced thing is visible as an unexplained broken link rather than as something the viewer may not see. | E |
| N11 | Two deliveries of one message produce two work items, with no note that a duplicate was seen. | A |
| N12 | A restarted system asks a human the same question twice, or applies one answer twice. | F |
| N13 | A credential belonging to a person is used by automation in a way that makes it look like that person acted. | K |
| N14 | Work is lost because a channel was renamed, a ticket closed, or a person left. | F |
| N15 | The audit trail contains the raw credential, or omits the decision that made an action lawful. | H |
| N16 | A person cannot find out, from the system, why the system did the thing it just did. | I |
| N17 | An unattended automation accumulates waiting-for-humans items that nobody owns. | C |
| N18 | A wide authority is granted as a convenience, and never revisited because nobody can see it. | C |
| N19 | An automated participant acts with no identity a team member can look up, so a record says only that "something" did it. | K |
| N20 | Automated work runs beside the team's process instead of through it — skipping review, checks, or conventions because it is automated. | K |
| N21 | Automated volume is invisible to planning: work that got done appears in no load, throughput, or spend figure, so its failure mode is invisible too. | K |
| N22 | A contributor is credited as the accountable party for an action, ending the trail at a machine instead of at a person or role. | L |
| N23 | A held action sits with its question never shown to anyone entitled to answer it. | C |
| N24 | A consent request carries too little to decide on, so approving means approving whatever happens next. | C |
| N25 | Consent requests arrive at a rate that teaches reflexive approval; nothing throttles, groups, or protects the approver's attention. | C |
| N26 | A retry repeats a visible side effect: a second comment, a second outward message, a second deployment. | F |
| N27 | One upstream fault fans out into items, consents, and alerts at every team at once, ungrouped. | F |
| N28 | A stalled queue is quiet: work waiting for a capacity that never returns tells no one. | F |
| N29 | The system's own write (comment, change, label) re-enters as new input and triggers work again. | B |
| N30 | A queued instruction executes after its moment has passed — after a cancellation, a closure, a hand-off — with nothing checking the context still stands. | F |
| N31 | Two automated participants make overlapping changes and the clash surfaces only after it is visible. | K |
| N32 | Every automated participant shares one identity, so records separate human from machine but nothing more. | K |
| N33 | A contributor's self-report ("checks passed", "nothing broken") is recorded as verified evidence rather than as an unverified claim. | K |
| N34 | A new permission rule silently changes what an older rule used to mean, with no note to anyone whose earlier action relied on the old reading. | C |
| N35 | A team rename, merge, or disband rewrites history so past decisions belong to a successor that did not exist when they were made. | G |
| N36 | A handover exposes more of the giving team's material than the receiving task needed. | G |
| N37 | An item's readable account and its durable trail disagree about the same facts. | F |
| N38 | "Completed" rests on the executor's word with nothing checkable attached to it. | E |
| N39 | Silencing one unwanted behaviour requires switching off a large part of the system. | I |
| N40 | A team's routine action exists only on the operator's surface, so the team needs central help to do its own work. | I |
| N41 | A forgetting rule is half-honoured: copies survive in summaries, quotes, or caches beyond the rule's reach. | H |
| N42 | One person becomes two participants across systems, or two become one, and standing, notifications, and history split accordingly. | B |
| N43 | Work items wait on each other in a cycle, or a wait chain outgrows anyone's ability to follow it, and neither is flagged. | A |
| N44 | Held-action content reaches an unentitled reader sideways — a shared preview, a notification body — where the item itself would have refused. | G |

---

## 15. Coverage checklist for the audit

Use this as the completeness check after the scenarios. Each cell should be answerable by running
something, not by reading a document.

| | ask | follow-up | question to human | consent | outcome | failure |
|---|---|---|---|---|---|---|
| open channel | | | | | | |
| thread / topic | | | | | | |
| ticket comment | | | | | | |
| direct message | | | | | | |
| operator view | | | | | | |
| application interface | | | | | | |
| clock | | | | | | |
| external event | | | | | | |
| another work item | | | | | | |

Cross-cutting questions that no single scenario covers:

- Which of these can a person complete **alone**, without an operator's help?
- Which produce an artefact a stranger can read a year later and understand?
- Where does the system know something about a person that the person has not proven?
- Which states are terminal, and can any of them be revived by accident?
- For every "it is allowed", can the system name the rule and the person who stands behind it?
- What happens at scale: 200 items, 40 approvals, 9 teams, one incident?

---

## 16. Verdict vocabulary

When auditing, record exactly one verdict per scenario plus the observable:

| Verdict | Meaning |
|---|---|
| **covered** | The expected behaviour happens, and every `Probe:` edge behaves sensibly. |
| **partial** | The main path works, a named `Probe:` edge does not. |
| **absent** | Nothing recognisable happens. |
| **silent-absent** | Nothing happens *and* nothing indicates that anything was expected. Worst class. |
| **divergent** | Something happens, but it disagrees with what a person would expect. |
| **unexpressible** | The system's model cannot represent the scenario, so it cannot be fixed by configuration. |
| **environment-blocked** | Not testable here; recorded with the reason and re-run later. |

Prefer **silent-absent** over **absent** wherever a person would be left wondering — the gap class
matters more than the gap.
