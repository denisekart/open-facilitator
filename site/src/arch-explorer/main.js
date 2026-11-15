// The explorer bundle's only export surface: window.OfacArchExplorer.
// app.js calls mount() when it exists; without JS (or without this
// bundle), the static SVG snapshot stands on its own. IIFE format: this
// module has no exports, it only installs the global.

import { mount, unmount } from "svelte";
import ArchExplorer from "./ArchExplorer.svelte";
import "@xyflow/svelte/dist/style.css";

globalThis.OfacArchExplorer = {
  mount(el, { data, focus = null }) {
    const app = mount(ArchExplorer, { target: el, props: { data, focus } });
    return { destroy: () => unmount(app) };
  },
};
