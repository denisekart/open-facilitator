# `@ofac/ui` — embedded admin SPA

The operator console: one responsive SPA served by the kernel
itself (no separate portal). Svelte 5 runes + vite; no component library —
the handful of primitives live in `src/components/`.

Design rules that keep it coherent (all in `src/app.css` + `src/lib/status.ts`):

- One vocabulary for machine states — `stateLabel`/`stateTone` render every
  status as a plain word plus one dot of semantic color (never hue alone);
  `ago()` shows relative time with the exact UTC stamp in the tooltip.
- The "Ink on Paper" token set (the design-language skill is the law):
  light is the default, dark follows the OS, the header toggle persists the
  choice (`lib/theme.svelte.ts`), `index.html` applies it before first
  paint, and the hexes are shared with the public site (parity law).
- One 44px chrome frame on every route: the view menu is a horizontal
  strip past 720px and a left drawer behind the hamburger at or under it;
  the AiDeck opens as a right drawer (Esc closes, focus returns).
- Keyboard: `/` focuses search, Esc releases it, every control has a visible
  focus ring; wide tables scroll inside themselves below 720px.

## Build & embed

`pnpm -r build` (or `pnpm --filter @ofac/ui build`) emits hashed assets to
`../wwwroot/` — pure build output, git-ignored, vite-emptied each build.
The kernel serves it (`UseDefaultFiles` + `UseStaticFiles` +
`MapFallbackToFile("index.html")`) and bootstraps the SPA from
`GET /ui-config` (anonymous: `{oidcAuthority, oidcClientId}`). A kernel
without `wwwroot` boots fine — only `/` 404s; every build path
(`make build`, the Dockerfile's own `ui` stage, CI `admin-e2e`) stages it
before the kernel build/publish.

## Dev loop

Kernel on 8088 + Keycloak on 8480 (both from the slim reference env:
`cd reference && ./referencectl up slim`), then:

```bash
pnpm -C src/facilitator/ui dev   # http://localhost:5173
```

The vite dev server proxies `/api` and `/ui-config` to 8088, so the OIDC
origin stays same-site (the `facilitator-api` public client allows any
origin; PKCE is client-side). Sign in `alice`/`alice`.

Unit tests: `pnpm -C src/facilitator/ui test` (vitest, jsdom — covers the
ProblemError mapping and the silent-renew 401 replay). Types:
`pnpm -C src/facilitator/ui typecheck` (svelte-check, threshold error).
Browser tier: `make admin-e2e` at repo root.

## Headless poking (no browser)

Every view is a thin consumer of `/api/admin/*`. Drive it with a service
token instead of the SPA:

```bash
ATOK=$(./reference/referencectl token | tail -1)   # facilitator-cli
curl -s -H "Authorization: Bearer $ATOK" http://localhost:8088/api/admin/summary | jq
curl -s -H "Authorization: Bearer $ATOK" "http://localhost:8088/api/admin/search?q=report" | jq '.[0]'
```

## Auth model

`lib/auth.svelte.ts` holds the session (`oidc-client-ts` UserManager,
authorization-code + PKCE, silent renew via prompt-none iframe; access
token in memory/sessionStorage — no custom token store). Roles come from
the **access token** payload (`realm_access.roles` ∪ `groups`), which is
exactly what the kernel's `IngressRoleGateMiddleware` evaluates — so
`session.canMutate` mirrors server authorization instead of a client-side
guess. Edit affordances (config PATCH, replay, re-mark) render only for
principals holding `admin.mutate`.
