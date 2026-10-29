// Client-only SPA: no adapter, no SSR — vite builds index.html + assets for
// the kernel's wwwroot. Compile-time runes mode is the Svelte 5 default.
const config = {
  compilerOptions: {},
};

export default config;
