import { mount } from "svelte";

import App from "./App.svelte";
import "./app.css";
import { startRouting } from "./lib/route.svelte.js";
import { initTheme } from "./lib/theme.svelte.js";

// Before mount, outside any reactive graph: reading/writing theme state from
// a component $effect would make that effect re-run on the theme write.
initTheme();
// Same law for the address bar: the first route write must not come from a
// component $effect, or every view would re-run on navigation.
startRouting();

const target = document.getElementById("app");
if (!target) {
  throw new Error("#app mount point missing");
}

export default mount(App, { target });
