# Identity claim-mapper error

**Symptom.** Tokens validate (no 401) but permissions are wrong: a human
admin gets 403 on a plane they own, or a workload token is admitted
*without* a role it should have. The mapper is the kernel's
`identity:claims` startup config (which claim path points at roles) plus
the `identity_roles` rules editable through
`GET/PUT /api/identity/roles` (each rule `{claim: "/json/path", op,
value}` with optimistic `rowVersion`). A mis-pointed claim pointer or a
bad rule looks identical to "auth broken" — it is authorization, and the
surface above shows which rule fired.

## Law (from shipped code)

Two distinct failure modes with distinct status codes: an invalid or
tampered bearer is **401** at `LinkAuthMiddleware`/bearer handlers —
zero processing, zero inbox rows, ProblemDetails
`https://errors.openfacilitator.org/v1/unauthorized`. A valid token whose
claims don't satisfy the operation's role requirement is **403**
`forbidden`, `detail` naming the missing role set ("operation
'work.write' requires one of the roles: kernel-operator"). Role rules
evaluate claim JSON-pointer paths against the token, are versioned
(`rowVersion`) and audited on change; link-plane admission additionally
passes `AdmissionGate` (enrollment + capability manifest law).

## Diagnosis

```bash
HUMANTOK=$(./referencectl token --user alice --password alice)   # raw JWT
# what does the token actually claim?
echo "$HUMANTOK" | cut -d. -f2 | base64 -d | jq '.realm_access, .scope'
# which rules map claims -> roles, and at what version?
curl -s -H "Authorization: Bearer $HUMANTOK" http://localhost:8089/api/identity/roles | jq
# the 403 body names the required role set verbatim
curl -s -X POST -H "Authorization: Bearer $HUMANTOK" -H 'content-type: application/json' \
  -d @event.json http://localhost:8089/api/ingress/events | jq '.detail'
```

```sql
-- role rules as persisted (the API surface is the writer)
SELECT * FROM facilitator.identity_roles;
-- did a rejected request touch the store at all? (401 must be zero)
SELECT count(*) FROM facilitator.inbox WHERE event_id = '<suspect-id>';
```

## Action

For a wrong 403: compare the token's `realm_access.roles` (or the
configured claim pointer) against the rule that should have granted the
role — fix the *pointer* in `identity:claims` config (restart required)
or the *rule* via `PUT /api/identity/roles` with a fresh `rowVersion`
(stale version → 409, no silent overwrite). For an unexpected 401: check
clock skew, audience, and the IdP signing keys. For link-plane endpoints,
enrollment state (see gateway-offline) gates before roles. Never widen a
rule "to make it work": the 403 detail names exactly the role the caller
lacks, and the fix belongs on the principal side unless the pointer is
provably wrong.

## Proven behavior (auth failures + dedupe, end to end)

Rehearsed against the reference stack's LB front door:

- Tampered bearer (`Bearer XXINVALID`) ⇒ **401** (`unauthorized`,
  ProblemDetails), and **zero** rows written for that event — a rejected
  request leaves no trace in the store, as the law requires.
- A schema-valid event with a valid operator token ⇒ **202**
  `{"status":"accepted","inboxId":…}`; the identical envelope (same
  CloudEvents `id`) again ⇒ **200** `{"status":"duplicate"}` carrying
  *the original's* `inboxId`. Final state: exactly one inbox row,
  `state=processed`. Dedupe keys are the envelope identity, so replay is
  provably side-effect-free.
- A valid token whose roles don't meet the operation gate ⇒ **403**
  `forbidden` naming the requirement (e.g. "operation 'work.write'
  requires one of the roles: kernel-operator"), zero processing.
  `GET /api/identity/roles` shows the live claim-pointer rules (plus
  `rowVersion`) that decided it — the 403 and the role table together
  are the full diagnosis.
