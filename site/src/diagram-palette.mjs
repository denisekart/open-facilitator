// One diagram palette, two themes.
//
// Mermaid computes its colors ONCE — at build time (SSR chromium) or on the
// client fallback path — so the emitted SVG carries literal hexes baked from
// the DARK instrument set. This module rewrites those hexes to CSS custom
// properties after rendering: an inline SVG resolves var() against the
// document, so every figure retints with the site's theme toggle instantly,
// no re-render, no layout shift. Dark mode re-derives the very hexes mermaid
// rendered, so dark looks exactly as before; light mode flips the --dia-*
// values (both legs defined in styles.css).
//
// Dark rendering set (themeVariables, both SSR and client):
//   node #2b313b · node-alt #323944 · tint #3d332d · cluster #3f4754
//   ink #f3f4f6 · line #9ca3af · error ink #cdc6bb · loop #eaeaea
//   surface #f4f4f4 · note #fff5ad · note ink #333/#333333 · loop #666
//   actor-line #999 · copper #c96a2d + white seq numbers are
//   theme-invariant and NOT mapped.
//
// Carriers handled: the per-diagram <style> block (hex -> var()) and
// presentation attributes fill="#…"/stroke="#…" (var() is NOT legal in a
// presentation attribute, so the hex is MOVED into the element's style
// attribute). Copper and the shadow flood ride along untouched.

// [dark literal, var name]. Longer hexes first; the 3-digit legs are
// boundary-guarded so they never clip a longer token mid-pass.
export const DIAGRAM_VAR_MAP = [
  ["#2b313b", "--dia-node"],
  ["#323944", "--dia-node-alt"],
  ["#3d332d", "--dia-tint"],
  ["#3f4754", "--dia-cluster-border"],
  ["#f3f4f6", "--dia-ink"],
  ["#9ca3af", "--dia-line"],
  ["#cdc6bb", "--dia-error-ink"],
  ["#eaeaea", "--dia-loop"],
  ["#f4f4f4", "--dia-surface"],
  ["#fff5ad", "--dia-note"],
  ["#edf2ae", "--dia-note-seq"],
  ["#e0e0e0", "--dia-alt"],
  ["#0b0b0b", "--dia-arrowhead"],
  ["#000000", "--dia-marker"],
  ["#000", "--dia-marker"],
  ["#ffffff", "--dia-seq"],
  ["#333333", "--dia-note-ink"],
  ["#333", "--dia-note-ink"],
  ["#666666", "--dia-loop-border"],
  ["#666", "--dia-loop-border"],
  ["#999999", "--dia-line-faint"],
  ["#999", "--dia-line-faint"],
];

const VAR_BY_HEX = new Map(DIAGRAM_VAR_MAP.map(([h, v]) => [h.toLowerCase(), v]));

const toVar = (hex) => VAR_BY_HEX.get(hex.toLowerCase());

// 1) Presentation attributes: <rect fill="#eaeaea" style="x:y"> must become
//    <rect style="x:y;fill:var(--dia-loop)">. Only touches mapped colors.
// An SVG presentation attribute LOSES to any author CSS rule, and
// mermaid's per-diagram <style> block carries a class rule for nearly
// every shape — moving those attributes into inline styles would promote
// a dead hex over the live themed rule (that is what washed out the
// sequence actor boxes). So only move attributes on CLASSLESS elements,
// where the attribute is the only color source; classed elements keep
// their (already var-mapped) class rules and the dead hex rides along
// harmlessly on the attribute.
function moveAttrHexesToStyle(svg) {
  return svg.replace(/<(rect|path|circle|line|polygon|text|ellipse|feDropShadow|feFlood|marker)\b[^>]*>/g, (tag) => {
    if (/\sclass="/i.test(tag)) return tag;
    let out = tag;
    const inject = [];
    for (const attr of ["fill", "stroke"]) {
      const re = new RegExp(`\\s${attr}="#([0-9a-fA-F]{3,6})"`, "i");
      out = out.replace(re, (m, hex) => {
        const v = toVar("#" + hex);
        if (!v) return m;
        inject.push(`${attr}:var(${v})`);
        return "";
      });
    }
    if (!inject.length) return tag;
    const decl = inject.join(";");
    if (/\sstyle="[^"]*"/i.test(out)) {
      out = out.replace(/\sstyle="([^"]*)"/i, (m, css) => ` style="${css.replace(/;+\s*$/, "")};${decl}"`);
    } else if (/\s*\/>$/.test(out)) {
      out = out.replace(/\s*\/>$/, ` style="${decl}"/>`);
    } else {
      out = out.replace(/>$/, ` style="${decl}">`);
    }
    return out;
  });
}

// 2) <style> blocks and inline style bodies: plain hex -> var().
function hexesToVars(svg) {
  let out = svg;
  for (const [hex, v] of DIAGRAM_VAR_MAP) {
    const guard = hex.length === 4 ? "(?![0-9a-fA-F])" : "";
    out = out.replace(new RegExp(hex + guard, "gi"), `var(${v})`);
  }
  return out;
}

export function retintDiagram(svg) {
  return hexesToVars(moveAttrHexesToStyle(svg));
}
