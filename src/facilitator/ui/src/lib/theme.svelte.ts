// Theme preference for the console: light is the default (operators sit under
// office lights), dark follows the OS until the toggle overrides it, and the
// override persists per browser. initTheme() runs once from main.ts, BEFORE
// mount — calling it from a component $effect would make that effect re-run
// on its own state write (and re-spend the OIDC auth code, breaking sign-in).

const KEY = "ofac-ui-theme";

function preferred(): "light" | "dark" {
  const stored = localStorage.getItem(KEY);
  if (stored === "light" || stored === "dark") return stored;
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

export const theme = $state({ value: "light" as "light" | "dark" });

export function initTheme(): void {
  theme.value = preferred();
  document.documentElement.dataset.theme = theme.value;
}

export function toggleTheme(): void {
  theme.value = theme.value === "dark" ? "light" : "dark";
  localStorage.setItem(KEY, theme.value);
  document.documentElement.dataset.theme = theme.value;
}
