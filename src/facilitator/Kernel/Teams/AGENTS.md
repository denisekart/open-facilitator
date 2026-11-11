# src/facilitator/Kernel/Teams

## Purpose
Who belongs to a team, in what capacity, and what a team's switches say. The
human/team layer every other area resolves authority and cadence against.

## Truth anchors
- `TeamRosterStore.cs` — `facilitator.team_members` reads/writes; an empty
  roster means **unconfigured** (allowed), a populated roster gates.
- `TeamSettingsStore.cs` — `facilitator.team_settings`: version-fenced,
  attributed, reasoned per-scope switch rows (team and channel scopes).
- `EffectiveConfigResolver` (in `TeamRosterStore.cs`) — the ONE cross-scope
  switch answer: system (`admin_config`) → team → channel, most specific wins,
  and every answer names the level it came from.
- `TeamScope.cs` — team-scope read authority; a foreign team's private resource
  is answered not-found, never a confirming 403. Team slugs come from
  `/teams/<slug>` groups.
- `TeamEndpoints.cs` — `/api/teams` surface. An operator passes anywhere; a
  team-operator (a roster row of capacity `operator`) only its own team.
- Built-in role names live in `../Shared/Identity/BuiltInRoles.cs`: `operator`
  and `team-operator` are code-declared, config cannot remove them.
- Config bootstraps both planes at startup (`../Shared/Identity/TeamRosterSeeder.cs`);
  the DB is truth after the first write.

## Invariants
- The roster is the only source of team standing; a display name never enters
  an actor string.
- A switch that resolves at no level is absent, not defaulted in code — the
  reader decides what absent means.
- Every roster/switch write is audited with author, reason and revision.
