// Evidence capture: operator-console screenshots for .omp/evidence.
//   node tools/capture-evidence.mjs [--only=desktop-light,desktop-dark,mobile-light,mobile-dark]
// Requires the reference env up (`./referencectl up slim`). Logs in as alice
// (framework-admin + ai-operator) through the real Keycloak flow; the 9x
// read-only shots log in as bob. Theme rides localStorage `ofac-ui-theme`.
/* global document, location, localStorage, window, getComputedStyle */ // page.evaluate callbacks run in the browser
import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const OUT = path.join(ROOT, ".omp", "evidence");
const BASE = process.env.FACILITATOR_URL ?? "http://localhost:8088";
const PW = path.join(
  ROOT,
  "tests/Facilitator.E2E/bin/Release/net10.0/.playwright/package/index.js",
);
const { chromium } = require(PW);

const onlyArg = process.argv.find((a) => a.startsWith("--only="));
const ONLY = onlyArg ? onlyArg.split("=")[1].split(",") : null;

const VARIANTS = [
  { name: "desktop-light", theme: "light", viewport: { width: 1440, height: 900 } },
  { name: "desktop-dark", theme: "dark", viewport: { width: 1440, height: 900 } },
  {
    name: "mobile-light",
    theme: "light",
    viewport: { width: 390, height: 844 },
    mobile: true,
  },
  {
    name: "mobile-dark",
    theme: "dark",
    viewport: { width: 390, height: 844 },
    mobile: true,
  },
];

let failures = 0;
async function shot(page, v, n, opts = {}) {
  try {
    await page.waitForTimeout(opts.settle ?? 350);
    await page.screenshot({
      path: path.join(OUT, `${v.name}-${n}.png`),
      fullPage: !!opts.full,
    });
    console.log(`  ok ${v.name}-${n}`);
  } catch (e) {
    failures++;
    console.log(`  FAIL ${v.name}-${n}: ${String(e).split("\n")[0]}`);
  }
}
// Best-effort step: a missing control is reported, never fatal.
async function step(page, label, fn) {
  try {
    await fn();
    return true;
  } catch (e) {
    failures++;
    console.log(`  SKIP ${label}: ${String(e).split("\n")[0].slice(0, 140)}`);
    return false;
  }
}
const t = (id) => `[data-testid="${id}"]`;

async function clearOverlays(page) {
  // The user-menu popover and boot toasts sit over the whole viewport
  // (fixed scrim / z-index): close menu, then remove any toast so the
  // evidence shows the screens, not the dismissal chrome.
  await page.keyboard.press("Escape");
  // The mobile nav drawer's scrim is a button, but the open rail
  // intercepts pointer events over it — invoke it directly.
  await page.evaluate(() => {
    document.querySelector('[aria-label="close the view menu"]')?.click();
  });
  await page
    .locator('[aria-label="dismiss notification"]')
    .first()
    .click({ timeout: 1500 })
    .catch(() => {});
  await page.evaluate(() => {
    document
      .querySelectorAll("[role='status'],.toast,[aria-label='dismiss notification']")
      .forEach((n) => {
        const box = n.closest(".fixed") ?? n;
        if (box !== n && getComputedStyle(box).position === "fixed") box.remove();
      });
    document.querySelectorAll(".scrim").forEach((n) => n.remove());
  });
  await page.waitForTimeout(150);
}

async function login(page, user, pass) {
  await page.goto(BASE, { waitUntil: "networkidle" });
  await page.locator(t("login")).click();
  await page.waitForURL("**/realms/facilitator/**");
  await page.fill('input[name="username"]', user);
  await page.fill('input[name="password"]', pass);
  await page.click("#kc-login");
  await page.waitForURL(`${BASE}/**`);
  // The chip is max-md:hidden on mobile viewports — attached, not visible.
  await page.locator(t("identity-chip")).waitFor({ state: "attached" });
}

const NAV = {
  "#/works": "view-works",
  "#/flows": "view-flows",
  "#/monitor/runs": "view-monitor",
  "#/monitor/deliveries": "view-deliveries",
  "#/monitor/audit": "view-audit",
  "#/monitor/runtime": "view-runtime",
  "#/team": "view-team",
  "#/config": "view-config",
  "#/chat": "view-chat",
};

// Real nav clicks (the SPA heals its own hash; programmatic hash writes
// race the deferred hashchange). Off-menu targets: set the hash, then
// reload so the app boots straight into the route (session rides
// sessionStorage, so login survives).
async function gotoHash(page, hash, mobile = false) {
  const nav = NAV[hash.split("?")[0]];
  if (nav) {
    if (mobile) {
      // Mobile: the rail is off-canvas; drive it through the drawer.
      await page
        .locator(t("menu-toggle"))
        .click()
        .catch(() => {});
      await page.locator(t(nav)).last().waitFor();
      await page.locator(t(nav)).last().click();
    } else {
      await page.locator(t(nav)).first().click();
    }
  } else {
    await page.evaluate((h) => {
      location.hash = h;
    }, hash);
    await page.reload({ waitUntil: "networkidle" });
  }
  await page.waitForTimeout(500);
}

async function chatSend(page, text) {
  await page.locator(t("chat-input")).fill(text);
  await page.locator(t("chat-send")).click();
}

async function waitReply(page) {
  await step(page, "chat reply", async () => {
    await page.locator(t("chat-md")).last().waitFor({ timeout: 30000 });
  });
}

async function captureDeck(page, v, prefix) {
  // The deck is chrome over every screen: brief it on Works, converse,
  // inspect its history/settings surfaces.
  await gotoHash(page, "#/works", v.mobile);
  await step(page, "floating toggle", async () => {
    await page.locator(t("floating-ai-deck-toggle")).waitFor();
    await shot(page, v, `${prefix}-aideck-floating-toggle`);
  });
  const opened = await step(page, "open deck", async () => {
    await page.locator(t("floating-ai-deck-toggle")).click();
    await page.locator(t("ai-deck")).waitFor();
  });
  if (!opened) return;
  await shot(page, v, `${prefix}-aideck-docked-briefed`);
  await step(page, "starter filled", async () => {
    await page.locator(t("chat-input")).fill("list my blocked works");
    await shot(page, v, `${prefix}-aideck-starter-filled`);
    await page.locator(t("chat-send")).click();
    await shot(page, v, `${prefix}-aideck-thinking`, { settle: 120 });
    await waitReply(page);
    await shot(page, v, `${prefix}-aideck-answer`);
    await chatSend(page, "what can you do for me here?");
    await waitReply(page);
    await shot(page, v, `${prefix}-aideck-two-turns`);
  });
  await step(page, "screen actions", async () => {
    await page.locator(t("ai-deck-screen-actions")).waitFor();
    await shot(page, v, `${prefix}-aideck-page-actions`);
  });
  await step(page, "history one session", async () => {
    await page.locator(t("chat-toggle-history")).click();
    await page.locator(t("chat-history-sidebar")).waitFor();
    await shot(page, v, `${prefix}-aideck-history-one-session`);
    await page.locator(t("chat-toggle-history")).click();
  });
  await step(page, "second session", async () => {
    await page.locator(t("chat-new-session")).click();
    await chatSend(page, "show the runtime sweep state");
    await waitReply(page);
    await page.locator(t("chat-toggle-history")).click();
    await shot(page, v, `${prefix}-aideck-history-two-sessions`);
    await page.locator(t("chat-toggle-history")).click();
  });
  await step(page, "verbosity proactive", async () => {
    await page.locator(t("chat-verbosity")).selectOption("proactive");
    await page.waitForTimeout(400);
    await shot(page, v, `${prefix}-aideck-verbosity-proactive`);
    await page.locator(t("chat-verbosity")).selectOption("balanced");
  });
  await step(page, "slash palette", async () => {
    await page.locator(t("chat-input")).fill("/");
    await page.locator(t("chat-verb-palette")).waitFor();
    await shot(page, v, `${prefix}-aideck-slash-palette`);
    await page.locator(t("chat-input")).fill("");
  });
  if (!v.mobile) {
    // On mobile the deck is already a foreground modal — no dock/expand
    // affordance there.
    await step(page, "expanded", async () => {
      await page.locator(t("ai-deck-expand")).click();
      await shot(page, v, `${prefix}-aideck-expanded`);
      await page.locator(t("ai-deck-expand")).click();
    });
  }
}

async function captureVariant(browser, v) {
  console.log(`== ${v.name}`);
  const context = await browser.newContext({
    viewport: v.viewport,
    deviceScaleFactor: 1,
    isMobile: !!v.mobile,
    hasTouch: !!v.mobile,
  });
  await context.addInitScript((theme) => localStorage.setItem("ofac-ui-theme", theme), v.theme);
  const page = await context.newPage();

  // --- gate + shell (logged in as alice) --------------------------------
  await page.goto(BASE, { waitUntil: "networkidle" });
  await shot(page, v, "01-login-gate");
  await login(page, "alice", "alice");
  await gotoHash(page, "#/works", v.mobile);
  await page.locator(t("works-view")).waitFor({ timeout: 15000 });
  await clearOverlays(page);
  await shot(page, v, "02-shell-home");
  await step(page, "user menu", async () => {
    await page.locator(t("user-menu-toggle")).click();
    await shot(page, v, "03-user-menu-open");
    await page.keyboard.press("Escape"); // close the popover for good
    await page.waitForTimeout(200);
  });
  if (v.mobile) {
    await step(page, "mobile nav drawer", async () => {
      await page.locator(t("menu-toggle")).click();
      await shot(page, v, "30-mobile-nav-drawer");
      await clearOverlays(page); // Escape does not close the drawer; press its scrim
    });
  }
  await clearOverlays(page);

  // --- works -------------------------------------------------------------
  await shot(page, v, "04-works");
  await step(page, "works blocked filter", async () => {
    await clearOverlays(page); // mobile: the nav drawer's scrim can linger
    await page.locator(t("works-filter-blocked")).click();
    await shot(page, v, "05-works-filter-blocked");
    await page.locator(t("works-filter-all")).click();
    await page.waitForTimeout(300);
  });

  // --- flows -------------------------------------------------------------
  await gotoHash(page, "#/flows", v.mobile);
  await step(page, "flows", async () => {
    await page.locator(t("flows-view")).waitFor();
    await shot(page, v, "06-flows");
  });
  await step(page, "new flow editor", async () => {
    await page.locator(t("new-flow")).click();
    await page.waitForTimeout(900);
    await shot(page, v, "07-flow-editor-new");
  });

  // --- monitor ------------------------------------------------------------
  await gotoHash(page, "#/monitor/runs", v.mobile);
  await step(page, "monitor runs", async () => {
    await page.locator(t("monitor-view")).waitFor();
    await shot(page, v, "08-monitor-runs");
  });
  await gotoHash(page, "#/monitor/deliveries", v.mobile);
  await step(page, "deliveries", async () => {
    await page.locator(t("deliveries-view")).waitFor();
    await shot(page, v, "09-monitor-deliveries");
    const row = page.locator('[data-testid^="delivery-"]').first();
    await row.waitFor();
    await shot(page, v, "23-delivery-row");
  });
  await gotoHash(page, "#/monitor/audit", v.mobile);
  await step(page, "audit", async () => {
    await page.locator(t("audit-view")).waitFor();
    await shot(page, v, "10-monitor-audit");
    await page.locator(t("audit-filter-deny")).click();
    await shot(page, v, "11-monitor-audit-deny");
    await page.locator(t("audit-filter-all")).click();
  });
  await gotoHash(page, "#/monitor/runtime", v.mobile);
  await step(page, "runtime", async () => {
    await page.locator(t("runtime-view")).waitFor();
    await shot(page, v, "12-monitor-runtime");
    await page.evaluate(() => window.scrollBy(0, 600));
    await shot(page, v, "13-monitor-runtime-lower");
    await page.evaluate(() => window.scrollTo(0, 0));
  });

  // --- team + config -------------------------------------------------------
  await gotoHash(page, "#/team", v.mobile);
  await step(page, "team", async () => {
    await page.locator(t("links-view")).waitFor();
    await shot(page, v, "14-team");
    await page.locator(t("enrollment-tokens-card")).scrollIntoViewIfNeeded();
    await shot(page, v, "15-team-enrollment-tokens");
  });
  await gotoHash(page, "#/config", v.mobile);
  await step(page, "config", async () => {
    await page.locator(t("config-view")).waitFor();
    await shot(page, v, "16-config");
    await page.locator(t("config-open-retry-policy")).click();
    await page.waitForTimeout(600);
    await shot(page, v, "24-config-resource-edit");
    await step(page, "config save + history", async () => {
      // History renders only after a write; the resource may be
      // never-written, so fill both required int fields for a valid v1.
      await page.locator(t("field-maxAttempts")).fill("5");
      await page.locator(t("field-backoffCapSeconds")).fill("60");
      await page.locator(t("config-reason")).fill("evidence capture: audited policy write");
      await page.locator(t("config-save")).click();
      await page.locator(t("config-history")).waitFor();
      await shot(page, v, "25-config-resource-history");
      await page.locator('[data-testid^="rollback-v"]').first().click();
      await shot(page, v, "26-config-rollback-form");
    });
    await page.locator(t("config-close")).click();
  });

  // --- inspector ------------------------------------------------------------
  await gotoHash(page, "#/works", v.mobile);
  await step(page, "work inspector", async () => {
    // Open the seeded demo work: it is the one the flow instances
    // reference, so the inspector shows the filled timeline + flow strip.
    const open = page.locator(t("work-open-ee91d893"));
    if (
      (await open.count()) === 0 ||
      !(await open
        .first()
        .isVisible()
        .catch(() => false))
    ) {
      await page.locator('[data-testid^="works-filter-"]').first().click();
      await page.waitForTimeout(400);
    }
    const target =
      (await open.count()) > 0 ? open.first() : page.locator('[data-testid^="work-open-"]').first();
    await target.scrollIntoViewIfNeeded();
    await target.click();
    await page.locator(t("work-inspector")).waitFor();
    await page.locator(t("timeline")).waitFor();
    await shot(page, v, "17-work-inspector");
    await step(page, "flow strip", async () => {
      await page.locator(t("work-flow-strip")).scrollIntoViewIfNeeded();
      await shot(page, v, "18-work-inspector-flow-strip");
    });
    await page.locator(t("inspector-close")).click();
  });
  await gotoHash(page, "#/monitor/runs", v.mobile);
  await step(page, "run detail", async () => {
    await page.locator('[data-testid="instance-list"] tr[role="button"]').first().click();
    await page.locator(t("flow-detail")).waitFor();
    await shot(page, v, "20-monitor-run-detail");
    await step(page, "step log", async () => {
      await page.locator(t("step-log")).first().click();
      await shot(page, v, "21-monitor-run-step-log");
    });
  });
  await step(page, "definition detail", async () => {
    const defId = await page
      .locator('[data-testid="flow-detail"] .mono, [data-testid="flow-detail"] code')
      .first()
      .textContent();
    await gotoHash(page, "#/flows", v.mobile);
    const id = defId?.trim() || (await page.locator(".table tbody tr td").first().textContent()); // first def id cell
    await gotoHash(page, `#/monitor/definitions/${(id ?? "").trim()}`, v.mobile);
    await page.locator(t("definition-detail")).waitFor();
    await shot(page, v, "19-flow-definition-detail");
  });

  // --- search ---------------------------------------------------------------
  await step(page, "search", async () => {
    await gotoHash(page, "#/works", v.mobile);
    await page.locator(t("search-input")).fill("triage");
    await page.locator(t("search-input")).press("Enter"); // the go button is sr-only
    await page.waitForTimeout(800);
    await shot(page, v, "39-search-results");
    const hit = page.locator('[data-testid^="hit-"]').first();
    await hit.waitFor();
    await hit.click();
    await page.waitForTimeout(600);
    await shot(page, v, "40-search-hit-detail");
  });

  // --- AI deck (focus) --------------------------------------------------------
  await clearOverlays(page);
  await captureDeck(page, v, "27");
  await step(page, "chat deeplink", async () => {
    // #/chat is not a screen: the router heals it by opening the deck
    // drawer over the current view.
    await page.evaluate(() => {
      location.hash = "#/chat";
    });
    await page.waitForTimeout(900);
    await page.locator(t("chat-view")).waitFor();
    await shot(page, v, "38-chat-deeplink", { full: v.mobile });
  });

  // --- read-only twin (bob) ---------------------------------------------------
  const ro = await browser.newContext({
    viewport: v.viewport,
    deviceScaleFactor: 1,
    isMobile: !!v.mobile,
    hasTouch: !!v.mobile,
  });
  await ro.addInitScript((theme) => localStorage.setItem("ofac-ui-theme", theme), v.theme);
  const roPage = await ro.newPage();
  await step(page, "readonly pair", async () => {
    await login(roPage, "bob", "bob");
    await gotoHash(roPage, "#/works", v.mobile);
    await roPage.locator(t("works-view")).waitFor();
    await shot(roPage, v, "90-readonly-works");
    await gotoHash(roPage, "#/monitor/runtime", v.mobile);
    await roPage.locator(t("runtime-view")).waitFor();
    await shot(roPage, v, "91-readonly-runtime");
  });
  await step(page, "definition history", async () => {
    // Admin-plane shot (bob cannot read flows): the second gallery
    // definition's full detail page, as alice.
    await gotoHash(page, "#/monitor/definitions/nudge-escalation", v.mobile);
    await page.locator(t("definition-detail")).waitFor();
    await shot(page, v, "92-definition-history", { full: true });
  });

  await context.close();
  await ro.close();
}

fs.mkdirSync(OUT, { recursive: true });
const browser = await chromium.launch();
for (const v of VARIANTS) {
  if (ONLY && !ONLY.includes(v.name)) continue;
  await captureVariant(browser, v);
}
await browser.close();
console.log(failures === 0 ? "ALL SHOTS OK" : `${failures} capture issues`);
process.exit(failures === 0 ? 0 : 1);
