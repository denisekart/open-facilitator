/* Reference-card generator. 4px grid, measured text, band layout, overflow asserted.
   node _gen.cjs */
const fs = require('fs');

/* ---------- colour maths: mix(a,b,p) => p of b ---------- */
const lum = (h) => {
  const c = [1, 3, 5].map((i) => parseInt(h.substr(i, 2), 16) / 255)
    .map((v) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
  return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
};
const cr = (a, b) => { const p = [lum(a), lum(b)].sort((x, y) => y - x); return +((p[0] + 0.05) / (p[1] + 0.05)).toFixed(2); };
const mix = (a, b, p) => {
  const q = (v) => parseInt(v.substr(1), 16), A = q(a), B = q(b);
  const f = (i) => Math.round(((A >> (16 - i * 8)) & 255) * (1 - p) + ((B >> (16 - i * 8)) & 255) * p);
  return '#' + [f(0), f(1), f(2)].map((v) => v.toString(16).padStart(2, '0')).join('');
};

const T = {
  L: { name: 'light', bg: '#f1f3f5', surface: '#ffffff', raise: '#ffffff', work: '#f1f3f5', ink: '#1a202c',
       muted: '#505a69', faint: '#7a8699', line: '#cfd6e0', accent: '#c96a2d', on: '#ffffff', tx: '#ac531c', rt: '#c96a2d',
       danger: '#ba3c3c', dtx: '#ba3c3c', dark: false },
  D: { name: 'dark', bg: '#232831', surface: '#2b313b', raise: '#323944', work: '#232831', ink: '#f3f4f6',
       muted: '#9ca3af', faint: '#6b7280', line: '#3f4754', accent: '#c96a2d', on: '#ffffff', tx: '#e58a45', rt: '#c96a2d',
       danger: '#c75454', dtx: '#c75454', dark: true },
};
for (const t of Object.values(T)) {
  t.soft = mix(t.line, t.work, 0.45);                       /* 45% toward the ground it floats on */
  t.hover = mix(t.ink, t.raise, 0.94);                      /* 6% ink */
  t.tint = mix(t.accent, t.dark ? t.surface : t.raise, 0.80);  /* 20% accent into the panel ground */
}
const PAGE = '#e9ecef';
const F = "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif";
const MONO = "ui-monospace, 'Cascadia Mono', Menlo, monospace";
const btnH = 28, ctlR = 4, ovR = 6, GRID = 4, MX = 24, GAP = 12;
const q4 = (v) => Math.round(v / GRID) * GRID;

/* ---------- text metrics: per-class advances in em, generous ---------- */
const NARROW = "iljt.,:;'|!ïŸ[]()- "; const WIDE = 'mwMW@%'; const UPPER = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
function charW(ch, mono) {
  if (mono) return 0.62;
  if (NARROW.includes(ch)) return 0.30;
  if (WIDE.includes(ch)) return 0.92;
  if (UPPER.includes(ch)) return 0.72;
  return 0.58;
}
const measure = (s, fs, { mono = false, w = 400 } = {}) =>
  Math.ceil([...String(s)].reduce((a, c) => a + charW(c, mono) * fs, 0) * (w >= 500 ? 1.04 : 1));

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
let bounds = null;

/* ---------- primitives, each returns {s,w,h,right} ---------- */
function txt(x, y, s, o = {}) {
  const fsz = o.fs || 12, wd = o.w || 400;
  const w = measure(s, fsz, { mono: o.mono, w: wd });
  const right = o.anchor === 'end' ? x : x + w;
  if (bounds && right > bounds) throw new Error('overflow x: ' + s + ' at ' + right + ' > ' + bounds);
  const a = o.anchor === 'middle' ? ' text-anchor="middle"' : o.anchor === 'end' ? ' text-anchor="end"' : '';
  return { s: '<text x="' + x + '" y="' + y + '" font-size="' + fsz + '" font-weight="' + wd + '"' +
    (o.mono ? ' font-family="' + MONO + '"' : '') + a + (o.ls ? ' letter-spacing="' + o.ls + '"' : '') +
    ' fill="' + (o.fill || T.L.ink) + '">' + esc(o.caps ? String(s).toUpperCase() : s) + '</text>',
    w: w, h: fsz * 1.3, right: right };
}
const box = (x, y, w, h, o = {}) => ({ s: '<rect x="' + x + '" y="' + y + '" width="' + w + '" height="' + h +
  '" rx="' + (o.rx || 0) + '"' + (o.filter ? ' filter="url(#sh)"' : '') + ' fill="' + (o.fill || 'none') + '"' +
  (o.stroke ? ' stroke="' + o.stroke + '" stroke-width="' + (o.sw || 1) + '"' : '') +
  (o.dash ? ' stroke-dasharray="' + o.dash + '"' : '') + '/>', w: w, h: h, right: x + w });
const lin = (x1, y1, x2, y2, o = {}) => ({ s: '<line x1="' + x1 + '" y1="' + y1 + '" x2="' + x2 + '" y2="' + y2 +
  '" stroke="' + (o.stroke || T.L.line) + '" stroke-width="' + (o.w || 1) + '"' +
  (o.dash ? ' stroke-dasharray="' + o.dash + '"' : '') + '/>', w: x2 - x1, h: o.v || 1, right: x2 });
const raw = (s, w, h) => ({ s: s, w: w, h: h || 0, right: w });

/* baseline-centred text inside a control of height h */
const mid = (y, h, fsz) => y + h / 2 + Math.round(fsz * 0.35);

const cap = (x, y, s, th) => txt(x, y, s, { fs: 11, w: 500, ls: 0.6, caps: true, fill: th.faint });
const note = (x, y, s, th) => txt(x, y, s, { fs: 11, fill: th.muted });

/* ---------- icons: 24-grid, 1.5 stroke, currentColor via fill colour arg ---------- */
const gi = (d, sw) => (x, y, ink, z) => {
  const size = z || 16;
  return raw('<svg x="' + x + '" y="' + y + '" width="' + size + '" height="' + size + '" viewBox="0 0 24 24">' +
    '<g fill="none" stroke="' + ink + '" stroke-width="' + (sw || 1.5) +
    '" stroke-linecap="round" stroke-linejoin="round">' + d + '</g></svg>', size, size);
};
const I = {
  search: gi('<circle cx="11" cy="11" r="6.5"/><path d="M15.8 15.8 20 20"/>'),
  plus: gi('<path d="M12 5v14M5 12h14"/>'),
  back: gi('<path d="M14.5 5.5 8 12l6.5 6.5"/>'),
  fwd: gi('<path d="M9.5 5.5 16 12l-6.5 6.5"/>'),
  chev: gi('<path d="m6 9.5 6 6 6-6"/>'),
  check: gi('<path d="m5 12.8 4.6 4.6L19 7"/>'),
  dash: gi('<path d="M5.5 12h13"/>'),
  close: gi('<path d="M6 6l12 12M18 6 6 18"/>'),
  dots: gi('<path d="M5.5 12h.01M12 12h.01M18.5 12h.01"/>', 2.2),
  trash: gi('<path d="M4.5 6.5h15M9 6.5V4h6v2.5M6.6 6.5 7.7 20h8.6l1.1-13.5M10.5 10v6M13.5 10v6"/>'),
  filter: gi('<path d="M4 6.5h16M7 12h10M10 17.5h4"/>'),
  panel: gi('<rect x="3.5" y="5" width="17" height="14" rx="2" fill="none"/><path d="M14 5v14"/>'),
  pencil: gi('<path d="M4.5 19.5h4L20 8l-4-4L4.5 19.5z" fill="none"/><path d="m14.6 5.4 4 4"/>'),
  folder: gi('<path d="M3.5 6.5h6l2 2.5h9v10.5h-17z" fill="none"/>'),
  file: gi('<path d="M6.5 3.5h7l4 4v13h-11z" fill="none"/><path d="M13.5 3.5v4h4"/>'),
  link: gi('<path d="M10 14a4 4 0 0 1 0-5.6l2-2a4 4 0 0 1 5.6 5.6l-1.2 1.2"/><path d="M14 10a4 4 0 0 1 0 5.6l-2 2A4 4 0 0 1 6.4 12l1.2-1.2"/>'),
  tag: gi('<path d="M4 4h7l9 9-7 7-9-9z" fill="none"/><circle cx="8" cy="8" r="1.2" fill="none"/>'),
  clock: gi('<circle cx="12" cy="12" r="7.5" fill="none"/><path d="M12 7.5V12l3 2.5"/>'),
  user: gi('<circle cx="12" cy="8.5" r="3.5" fill="none"/><path d="M5.5 20c1.3-3.4 3.7-5 6.5-5s5.2 1.6 6.5 5"/>'),
  eye: gi('<path d="M2.5 12S6 6.5 12 6.5 21.5 12 21.5 12 18 17.5 12 17.5 2.5 12 2.5 12z" fill="none"/><circle cx="12" cy="12" r="2.6" fill="none"/>'),
  eyeoff: gi('<path d="M4 4l16 16"/><path d="M9.7 6.9A9.9 9.9 0 0 1 12 6.5c6 0 9.5 5.5 9.5 5.5a17.6 17.6 0 0 1-2.8 3.3M6.3 8.4A16.9 16.9 0 0 0 2.5 12S6 17.5 12 17.5c1 0 2-.2 2.9-.5"/>'),
  gear: gi('<circle cx="12" cy="12" r="3" fill="none"/><path d="M12 3.4v2.3M12 18.3v2.3M4.7 7.6l2 1.1M17.3 15.3l2 1.1M4.7 16.4l2-1.1M17.3 8.7l2-1.1"/>'),
  star: gi('<path d="M12 4.2l2.4 4.9 5.4.8-3.9 3.8.9 5.4-4.8-2.6-4.8 2.6.9-5.4L4.2 9.9l5.4-.8z" fill="none"/>'),
  bell: gi('<path d="M6.5 10.5a5.5 5.5 0 0 1 11 0c0 3.8 1.5 5.3 1.5 5.3H5s1.5-1.5 1.5-5.3z" fill="none"/><path d="M10 18.6a2 2 0 0 0 4 0"/>'),
  lock: gi('<rect x="5.5" y="10.5" width="13" height="9" rx="2" fill="none"/><path d="M8.5 10.5V8a3.5 3.5 0 0 1 7 0v2.5"/>'),
  undo: gi('<path d="M8 8.5H4.2V4.7"/><path d="M4.6 8.7a7.4 7.4 0 1 1 1.2 6.6"/>'),
  redo: gi('<path d="M16 8.5h3.8V4.7"/><path d="M19.4 8.7a7.4 7.4 0 1 0-1.2 6.6"/>'),
  down: gi('<path d="M12 4.5v10.5M7.8 11.2 12 15.4l4.2-4.2M4.5 19.5h15"/>'),
  copy: gi('<rect x="8.5" y="8.5" width="11" height="11" rx="2" fill="none"/><path d="M5.5 15.5H5a1 1 0 0 1-1-1V5.5a1 1 0 0 1 1-1h9a1 1 0 0 1 1 1v.5"/>'),
  move: gi('<path d="M12 3.5v17M3.5 12h17M12 3.5 9.6 5.9M12 3.5l2.4 2.4M12 20.5l-2.4-2.4M12 20.5l2.4-2.4M3.5 12l2.4-2.4M3.5 12l2.4 2.4M20.5 12l-2.4-2.4M20.5 12l-2.4 2.4"/>'),
  sun: gi('<circle cx="12" cy="12" r="3.8" fill="none"/><path d="M12 2.6v2.2M12 19.2v2.2M3.6 12h2.2M18.2 12h2.2M6.1 6.1l1.6 1.6M16.3 16.3l1.6 1.6M6.1 17.9l1.6-1.6M16.3 7.7l1.6-1.6"/>'),
  moon: gi('<path d="M20 14.6A8.4 8.4 0 1 1 9.6 4.2 7 7 0 0 0 20 14.6z" fill="none"/>'),
  sort: gi('<path d="M7 4.5v15M7 19.5l-2.8-2.8M7 19.5l2.8-2.8M17 19.5v-15M17 4.5l-2.8 2.8M17 4.5l2.8 2.8"/>'),
  cmd: gi('<rect x="4" y="4" width="16" height="16" rx="3.5" fill="none"/><path d="M8.5 8.5v7h7"/>'),
  play: gi('<path d="M7.5 5.2 18.8 12 7.5 18.8z" fill="none"/>'),
  keyboard: gi('<rect x="2.5" y="6.5" width="19" height="11" rx="2" fill="none"/><path d="M6 10h.01M9 10h.01M12 10h.01M15 10h.01M18 10h.01M8 14h8"/>'),
};
const ICONS = Object.keys(I);

/* ---------- components (all return {s,w,h}) ---------- */
function chip(x, y, label, dot, th, o = {}) {
  const fsz = 12, h = 20, padL = dot ? 20 : 8;
  const lw = measure(label, fsz);
  const dw = o.divider ? measure(o.divider, fsz) : 0;
  const w = q4(padL + lw + 8 + (o.divider ? dw + 16 : 0));
  let s = box(x, y, w, h, { fill: th.raise, stroke: o.danger ? th.danger : th.soft, rx: ctlR }).s;
  if (dot) s += '<circle cx="' + (x + 11) + '" cy="' + (y + h / 2) + '" r="3" fill="' + dot + '"/>';
  s += txt(x + padL, mid(y, h, fsz), label, { fs: fsz, fill: o.danger ? th.dtx : th.muted }).s;
  if (o.divider) {
    const dx = x + padL + lw + 8;
    s += '<line x1="' + dx + '" y1="' + (y + 4) + '" x2="' + dx + '" y2="' + (y + 16) + '" stroke="' + th.soft + '"/>';
    s += txt(dx + 8, mid(y, h, fsz), o.divider, { fs: fsz, fill: th.muted }).s;
  }
  return { s: s, w: w, h: h };
}
function btn(x, y, label, th, o = {}) {
  const fsz = 13;
  label = String(label).toUpperCase();
  const iconW = o.icon ? 22 : 0;
  const w = o.w || q4(24 + iconW + measure(label, fsz, { w: 600 }) + label.length * 0.5);
  let s = '';
  if (o.primary) s += box(x, y, w, btnH, { fill: th.accent, rx: ctlR }).s;
  else if (o.hover) s += box(x, y, w, btnH, { fill: th.hover, rx: ctlR }).s;
  else if (o.quiet) s += box(x, y, w, btnH, { fill: th.raise, stroke: th.soft, rx: ctlR }).s;
  const ink = o.primary ? th.on : o.disabled ? th.faint : o.destruct ? th.dtx : o.strong ? th.ink : th.muted;
  if (o.icon) s += I[o.icon](x + 12, y + 6, ink).s;
  s += txt(o.icon ? x + 12 + 16 + 6 : x + w / 2, mid(y, btnH, fsz), label,
    { fs: fsz, w: 600, caps: true, ls: '0.5', fill: ink, anchor: o.icon ? 'start' : 'middle' }).s;
  return { s: s, w: w, h: btnH };
}
function field(x, y, w, th, o = {}) {
  let s = box(x, y, w, btnH, { fill: th.raise, stroke: o.error ? th.danger : th.soft, rx: ctlR }).s;
  if (o.focus) s += '<rect x="' + (x - 2) + '" y="' + (y - 2) + '" width="' + (w + 4) + '" height="' +
    (btnH + 4) + '" rx="6" fill="none" stroke="' + th.rt + '" stroke-width="2"/>';
  if (o.icon) s += I[o.icon](x + 10, y + 6, th.faint).s;
  s += txt(x + (o.icon ? 34 : 10), mid(y, btnH, 14), o.value, { fs: 14, fill: o.value ? th.ink : th.faint }).s;
  return { s: s, w: w, h: btnH };
}
function segmented(x, y, label, segs, sel, th) {
  const fsz = 13, h = btnH;
  const lw = label ? measure(label, fsz) + 16 : 0;
  const sw = segs.map((s) => q4(measure(s, fsz) + 24));
  const w = q4(lw + sw.reduce((a, v) => a + v, 0));
  let s = box(x, y, w, h, { fill: th.raise, stroke: th.soft, rx: ctlR }).s;
  let sx = x + lw, selX = 0;
  segs.forEach((sg, i) => {
    if (i === sel) selX = sx;
    if (i && sx !== x + lw) s += '<line x1="' + sx + '" y1="4" x2="' + sx + '" y2="' + (h - 4) +
      '" stroke="' + th.soft + '" transform="translate(0,' + y + ')"/>';
    sx += sw[i];
  });
  if (sel >= 0) {
    s += box(selX + 0.5, y + 1, sw[sel] - 1, h - 2, { fill: th.tint }).s;
    const uw = Math.max(12, measure(segs[sel], fsz) - 8);
    s += box(selX + (sw[sel] - measure(segs[sel], fsz)) / 2, y + h - 3, uw, 2, { fill: th.rt }).s;
  }
  if (label) s += txt(x + 8, mid(y, h, fsz), label, { fs: fsz, fill: th.muted }).s;
  sx = x + lw;
  segs.forEach((sg, i) => {
    s += txt(sx + sw[i] / 2, mid(y, h, fsz), sg, { fs: fsz, w: i === sel ? 500 : 400, anchor: 'middle',
      fill: i === sel ? th.ink : th.muted }).s;
    sx += sw[i];
  });
  return { s: s, w: w, h: h };
}


/* ---------- regions: a box with a cursor, so nothing can collide ---------- */
class Region {
  constructor(sheet, x, y, w) { this.sheet = sheet; this.x = x; this.w = w; this.y = y; this.top = y; }
  head(caption, th) { if (caption) this.sheet.node(cap(this.x, this.y + 12, caption, th)); this.y += 30; return this; }
  /* place a node at the cursor, advance vertically by its height + gap */
  put(n, gap = 6, xoff = 0) {
    const node = { s: n.s, w: n.w, h: n.h || 0 };
    this.sheet.parts.push('<g transform="translate(' + (this.x + xoff) + ',' + this.y + ')">' + n.s + '</g>');
    if (n.w > this.w) throw new Error('region width exceeded by ' + (n.w - this.w) + 'px: ' + n.s.slice(0, 50));
    this.y += (n.h || 12) + gap;
    return this;
  }
  /* place nodes side by side on one line */
  row(nodes, gap = 6, label) {
    let sx = 0, h = 0;
    for (const n of nodes) {
      this.sheet.parts.push('<g transform="translate(' + (this.x + sx) + ',' + this.y + ')">' + n.s + '</g>');
      sx += n.w + gap; h = Math.max(h, n.h || 12);
    }
    if (label) this.sheet.parts.push('<g transform="translate(' + (this.x + sx) + ',' + (this.y + 12) + ')">' +
      txt(0, 0, label, { fs: 10, fill: this.sheet.th ? this.sheet.th.faint : T.L.faint }).s + '</g>');
    if (sx - gap > this.w) throw new Error('region row exceeded by ' + (sx - gap - this.w) + 'px');
    this.y += h + 8;
    return this;
  }
  say(text, o = {}) {
    this.sheet.node(txt(this.x, this.y + 10, text, Object.assign({ fs: 10, fill: T.L.faint }, o)));
    this.y += 20; return this;
  }
  at(dy) { this.y += dy; return this; }
  bottom() { return this.y; }
}

const TH = T.L, DK = T.D, W = 1200, RIGHT = W - MX;

class Sheet {
  constructor(title, sub) {
    this.w = W; this.parts = []; this.bottom = 0;
    bounds = W - MX;
    this.parts.push(txt(MX, 34, title, { fs: 20, w: 600 }).s);
    this.parts.push(txt(MX, 56, sub, { fs: 12, fill: T.L.muted }).s);
    this.bottom = 68;
  }
  node(n) {
    if (n.right !== undefined && n.right > this.w - MX)
      throw new Error('x overflow past right margin: ' + n.s.replace(/<[^>]+>/g, '').slice(0, 40));
    this.parts.push(n.s);
    return n;
  }
  rect(x, y, w, h, o = {}) {
    this.parts.push('<rect x="' + x + '" y="' + y + '" width="' + w + '" height="' + h + '" rx="' + (o.rx || 0) +
      '" fill="' + (o.fill || 'none') + '"' + (o.stroke ? ' stroke="' + o.stroke + '"' : '') +
      (o.filter ? ' filter="url(#sh)"' : '') + '/>');
    this.bottom = Math.max(this.bottom, y + h);
    return { x: x, y: y, w: w, h: h };
  }
  region(x, y, w) { return new Region(this, x, y, w); }
  rule(y) {
    this.parts.push('<line x1="' + MX + '" y1="' + y + '" x2="' + (this.w - MX) + '" y2="' + y +
      '" stroke="' + T.L.line + '"/>');
    this.bottom = Math.max(this.bottom, y);
  }
  done() {
    bounds = null;
    const h = Math.ceil(this.bottom + 20);
    return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ' + this.w + ' ' + h + '" font-family="' + F +
      '">\n<defs><filter id="sh" x="-40%" y="-40%" width="180%" height="180%"><feDropShadow dx="0" dy="1" ' +
      'stdDeviation="1.5" flood-color="#000" flood-opacity="0.14"/></filter></defs>\n' +
      '<rect width="' + this.w + '" height="' + h + '" fill="' + PAGE + '"/>\n' + this.parts.join('\n') + '\n</svg>\n';
  }
}

/* ---------- shared composite nodes ---------- */
function pairHistory(th) {
  const w = 76;
  return { s: '<rect width="' + w + '" height="' + btnH + '" rx="' + ctlR + '" fill="' + th.raise + '" stroke="' + th.soft + '"/>' +
    '<line x1="' + w / 2 + '" y1="4" x2="' + w / 2 + '" y2="' + (btnH - 4) + '" stroke="' + th.soft + '"/>' +
    I.back(14, 6, th.ink).s + I.fwd(w / 2 + 14, 6, th.faint).s, w: w, h: btnH };
}
const LOGO = '<circle cx="8" cy="8" r="3" fill="none"/><circle cx="16.5" cy="11" r="2.2" fill="none"/>' +
  '<circle cx="10" cy="17" r="2.6" fill="none"/>' +
  '<path d="M10.6 9.2 14.6 10.2M8.6 10.9l1.1 3.8M14.7 13l-2.6 2.9" fill="none" stroke-linecap="round"/>';
function logoSvg(th, z, dot, grid) {
  return '<svg width="' + z + '" height="' + z + '" viewBox="0 0 24 24">' +
    (grid ? '<path d="M0 6h24M0 12h24M0 18h24M6 0v24M12 0v24M18 0v24" stroke="' + th.soft + '" stroke-width="0.2"/>' : '') +
    '<g stroke="' + th.ink + '" stroke-width="1.5">' + LOGO + '</g>' +
    (dot ? '<circle cx="16.5" cy="11" r="1" fill="' + th.rt + '"/>' : '') + '</svg>';
}

/* ============================ PALETTE ============================ */
function cardPalette() {
  const s = new Sheet('Palette — cool grounds, one warm signal',
    'Neutrals carry blue, never a warm cast. The accent is the only warm value, and it appears only where state does.');
  const toks = [['bg', 'app ground'], ['surface', 'header, strips'], ['raise', 'menus, panels'],
    ['ink', 'primary text'], ['muted', 'secondary'], ['faint', 'placeholder'],
    ['line', 'frames, rules'], ['soft', 'floating edge'], ['work', 'content area'],
    ['accent', 'state fills'], ['on', 'text on accent'], ['danger', 'destructive'],
    ['tx', 'accent in text'], ['rt', 'accent as edges'], ['tint', 'selected ground']];
  const step = 122, swW = 92, swH = 40, rowStep = swH + 58;

  function theme(th, top, panelH) {
    const swatchW = 6 * step - (step - swW);
    s.node(cap(MX, top, th.name, th));
    toks.forEach((tk, i) => {
      const col = i % 6, row = Math.floor(i / 6);
      const x = MX + col * step, y = top + 14 + row * rowStep;
      s.parts.push('<rect x="' + x + '" y="' + y + '" width="' + swW + '" height="' + swH + '" rx="' + ctlR +
        '" fill="' + th[tk[0]] + '" stroke="' + th.line + '" stroke-opacity="0.6"/>');
      s.node(txt(x, y + swH + 13, '--' + tk[0], { fs: 11, w: 500, mono: true, fill: th.ink }));
      s.node(txt(x, y + swH + 27, th[tk[0]], { fs: 11, mono: true, fill: th.muted }));
      s.node(txt(x, y + swH + 40, tk[1], { fs: 10, fill: th.faint }));
    });
    const gridBottom = top + 14 + 2 * rowStep;
    const px = MX + 6 * step;
    s.rect(px, top + 14, RIGHT - px, panelH, { fill: th.surface, stroke: th.line, rx: ovR });
    const r = s.region(px + 12, top + 14, RIGHT - px - 24);
    r.th = th;
    r.head('derived, live', th);
    r.row([{ s: '<rect width="118" height="24" rx="' + ctlR + '" fill="' + th.tint + '" stroke="' + th.soft + '"/>' +
      '<text x="12" y="16" font-size="12" font-weight="600" fill="' +
      th.ink + '">selected</text>', w: 118, h: 24 },
      chip(0, 0, 'Saved', th.muted, th), chip(0, 0, 'Saving…', th.rt, th)], 8);
    r.say('tint plus a weight shift, so state never rides colour alone', { fill: th.muted });
    r.row([chip(0, 0, 'Not saved', th.danger, th, { danger: true }),
      chip(0, 0, 'Saved', th.muted, th, { divider: 'Offline' })], 8);
    r.say('one pill for state; badges share the slot behind a divider', { fill: th.muted });
    r.at(4);
    r.row([btn(0, 0, 'Primary', th, { primary: true }), btn(0, 0, 'Quiet', th, { quiet: true }),
      btn(0, 0, 'Ghost', th, { strong: true })], 6);
    r.row([btn(0, 0, 'Delete', th, { destruct: true }),
      { s: '<rect width="150" height="' + btnH + '" rx="' + ctlR + '" fill="' + th.raise + '" stroke="' + th.soft + '"/>' +
        '<rect x="-2" y="-2" width="154" height="' + (btnH + 4) + '" rx="6" fill="none" stroke="' + th.rt +
        '" stroke-width="2"/><text x="12" y="19" font-size="12" fill="' + th.ink + '">focus 2px, offset 2px</text>',
        w: 150, h: btnH }], 10);
    r.say('a border means frame, field, or error, never decoration', { fill: th.faint });
    r.at(2);
    r.say('AA  ink ' + cr(th.ink, th.surface) + ':1   muted ' + cr(th.muted, th.surface) +
      ':1   accent-text ' + cr(th.tx, th.surface) + ':1', { fs: 11, mono: true, fill: th.muted });
    r.say('danger-text ' + cr(th.dtx, th.surface) + ':1   ring ' + cr(th.rt, th.raise) +
      ':1   faint ' + cr(th.faint, th.surface) + ':1', { fs: 11, mono: true, fill: th.muted });
    return { gridBottom: gridBottom, panelBottom: top + 14 + panelH, regionBottom: r.bottom() };
  }
  let y = 84;
  const a = theme(TH, y, 268);
  y = Math.max(a.gridBottom, a.panelBottom) + 18;
  s.rule(y); y += 18;
  const bandTop = y - 8;
  const b = theme(DK, y, 268);
  s.parts.splice(s.parts.length - 0, 0); /* ground painted below via insert */
  s.parts.unshift('<rect x="' + (MX - 12) + '" y="' + bandTop + '" width="' + (W - 2 * MX + 24) + '" height="' +
    (b.panelBottom - bandTop + 16) + '" rx="' + ovR + '" fill="' + DK.bg + '"/>');
  y = b.panelBottom + 24;
  s.node(txt(MX, y, 'Rejected #efeae1 ground, ochre and brown accents: warm neutrals and mud read as packaging. Ink on tint: light ' +
    cr(TH.ink, TH.tint) + ':1, dark ' + cr(DK.ink, DK.tint) + ':1.', { fs: 11, fill: T.L.muted }));
  s.bottom = Math.max(s.bottom, y + 8);
  return s.done();
}

/* ============================ TYPE ============================ */
function cardType() {
  const s = new Sheet('Type — system stack, locked scale',
    'Nothing fetches, so nothing reflows. Six sizes, three weights, 1.45 leading, prose capped near 70ch.');
  const rows = [[24, 600, 'display, one per screen at most'], [20, 500, 'empty-state and page titles'],
    [16, 500, 'panel and modal titles'], [14, 400, 'body: paragraphs, inputs, rows'],
    [13, 500, 'controls, menu items, dense rows'], [12, 400, 'meta, chips, micro-labels']];
  const LW = 552, DX = 620, DW = RIGHT - DX;
  function scale(th, x0, w0, top) {
    s.node(cap(x0, top, th.name + ' scale', th));
    let y = top + 22;
    for (const r of rows) {
      y += 36;
      s.node(txt(x0, y, r[2], { fs: r[0], w: r[1], fill: th.ink }));
      s.node(txt(x0 + w0, y, r[0] + ' / ' + r[1], { fs: 10, mono: true, fill: th.faint, anchor: 'end' }));
    }
    return y;
  }
  let top = 88;
  const lEnd = scale(TH, MX, LW, top);
  let y = lEnd + 30;
  s.parts.push('<line x1="' + MX + '" y1="' + (y - 14) + '" x2="' + (MX + LW) + '" y2="' + (y - 14) + '" stroke="' + TH.line + '"/>');
  s.node(cap(MX, y, 'weights: emphasis comes from ink, not from bold', TH)); y += 26;
  s.node(txt(MX, y, '400 running text, secondary lines in muted', TH)); y += 24;
  s.node(txt(MX, y, '500 labels, controls, active nav', { fs: 14, w: 500, fill: TH.ink })); y += 24;
  s.node(txt(MX, y, '600 brand and titles only', { fs: 14, w: 600, fill: TH.ink })); y += 32;
  s.node(cap(MX, y, 'micro-label: 12 / 500 / uppercase / +0.04em', TH)); y += 22;
  s.node(txt(MX, y, 'section heading in a panel', { fs: 12, w: 500, ls: 0.5, caps: true, fill: TH.faint }));
  s.node(txt(MX + 228, y, 'one per group, never nested', { fs: 10, fill: TH.faint }));
  y += 36;
  s.node(cap(MX, y, 'mono = machine-assigned values', TH)); y += 22;
  s.node(txt(MX, y, 'atom-7f3c19 · 2026-11-16T09:14Z · src/main.ts', { fs: 12, mono: true, fill: TH.muted }));
  y += 22;
  s.node(txt(MX, y, '1,204 · 87 · 9,000,312', { fs: 12, mono: true, fill: TH.muted }));
  s.node(txt(MX + 168, y, 'tabular-nums, right-aligned in columns', { fs: 10, fill: TH.faint }));
  y += 34;
  s.node(cap(MX, y, 'contrast: a hint is text, so it must pass AA', TH)); y += 22;
  s.node(txt(MX, y, 'Keyboard hint Ctrl+K in muted', { fs: 13, fill: TH.muted }));
  s.node(txt(MX + 224, y, cr(TH.muted, TH.surface) + ':1 passes', { fs: 11, mono: true, fill: TH.muted }));
  y += 22;
  s.node(txt(MX, y, 'Keyboard hint Ctrl+K in faint', { fs: 13, fill: TH.faint }));
  s.node(txt(MX + 224, y, cr(TH.faint, TH.surface) + ':1 fails, so never a hint', { fs: 11, mono: true, fill: TH.danger }));
  const lBottom = y;

  s.rect(DX - 12, 72, DW + 24, 452, { fill: DK.bg, rx: ovR });
  const dEnd = scale(DK, DX, DW - 8, 92);
  let d = dEnd + 28;
  s.node(cap(DX, d, 'links, selection, measure', DK)); d += 24;
  s.node(txt(DX, d, 'accent link, 1px underline, 2px offset', { fs: 13, fill: DK.tx }));
  const sw = measure('selection', 12);
  s.parts.push('<rect x="' + (RIGHT - 8 - sw - 16) + '" y="' + (d - 14) + '" width="' + (sw + 16) +
    '" height="19" rx="2" fill="' + DK.rt + '"/><text x="' + (RIGHT - sw - 8) + '" y="' + d +
    '" font-size="12" fill="' + DK.on + '">selection</text>');
  d += 20;
  s.rect(DX, d, DW - 8, 50, { fill: DK.surface, stroke: DK.line, rx: ctlR });
  s.node(txt(DX + 12, d + 21, 'A 14px line that runs the full measure is the fastest', { fs: 13, fill: DK.ink }));
  s.node(txt(DX + 12, d + 39, 'way to look un-designed. Cap it near 70ch.', { fs: 13, fill: DK.ink }));
  d += 66;
  s.rect(DX, d, DW - 8, 56, { fill: DK.work, stroke: DK.line, rx: ctlR });
  s.node(cap(DX + 12, d + 22, 'rejected', DK));
  s.node(txt(DX + 12, d + 46, '700 · 15px · −0.02em · UPPERCASE BUTTON LABELS', { fs: 14, w: 700, fill: DK.danger }));
  s.bottom = Math.max(s.bottom, Math.max(lBottom, d + 56) + 8);
  return s.done();
}

/* ============================ ICONS ============================ */
function cardIcons() {
  const s = new Sheet('Icons — 24-grid, currentColor, 1.5 stroke',
    'Round caps and joins, unfilled shapes, aria-hidden. Words label actions; a glyph stands alone only for a universal verb.');
  const set = ['search', 'plus', 'back', 'fwd', 'chev', 'check', 'dash', 'close', 'dots', 'sort', 'filter', 'panel',
    'pencil', 'file', 'folder', 'link', 'tag', 'clock', 'user', 'star', 'eye', 'eyeoff', 'bell', 'lock',
    'undo', 'redo', 'down', 'copy', 'move', 'gear', 'sun', 'moon', 'play', 'keyboard', 'cmd', 'trash'];
  const slot = 42, cols = 12, labelH = 44;
  function grid(th, x0, top, names, labelled) {
    s.node(cap(x0, top, th.name, th));
    names.forEach((n, i) => {
      const col = i % cols, row = Math.floor(i / cols);
      const x = x0 + col * slot, y = top + 14 + row * (labelH + 4);
      s.parts.push('<rect x="' + x + '" y="' + y + '" width="32" height="32" rx="' + ctlR + '" fill="' +
        th.surface + '" stroke="' + th.soft + '"/>');
      s.parts.push(I[n](x + 8, y + 8, th.ink).s);
      if (labelled) s.node(txt(x + 16, y + 42, n, { fs: 9, fill: th.faint, anchor: 'middle' }));
    });
    return top + 14 + Math.ceil(names.length / cols) * (labelH + 4);
  }
  let y = 84;
  /* light grid left 12 cols; dark grid right 12 cols, a subset */
  const lightBottom = grid(TH, MX, y, set, true);
  const darkSet = set.slice(0, 24);
  const darkBottom = grid(DK, 620, 84, darkSet, true);
  s.parts.splice(2, 0, '<rect x="' + 604 + '" y="68" width="' + (RIGHT - 604 + 12) + '" height="' +
    (darkBottom - 52) + '" rx="' + ovR + '" fill="' + DK.bg + '"/>');
  s.node(txt(620, darkBottom + 8, 'same paths, ink from tokens. State glyphs are shapes, so greyscale survives.',
    { fs: 10, fill: DK.muted }));
  y = Math.max(lightBottom, darkBottom + 20) + 4;

  s.rule(y); y += 20;
  s.node(cap(MX, y, 'active indicator: a 2px rule, label width, no ornament', TH));
  let nav = MX, ny = y + 26;
  for (const [i, l] of ['Library', 'Graph', 'Settings'].entries()) {
    const n = txt(nav, ny, l, { fs: 14, w: i === 0 ? 500 : 400, fill: i === 0 ? TH.ink : TH.muted });
    s.node(n);
    if (i === 0) s.parts.push('<rect x="' + nav + '" y="' + (ny + 8) + '" width="' + (n.w - 8) + '" height="2" fill="' + TH.rt + '"/>');
    nav += n.w + 24;
  }
  s.node(txt(MX, ny + 30, 'a rule survives forced-colors, a tint would not', { fs: 10, fill: TH.faint }));

  const lx = 470;
  s.node(cap(lx, y, 'logo rules: 24-grid, one weight, ≤4 shapes, ≤2 colours, a circle + line vocabulary', TH));
  s.parts.push('<g transform="translate(' + lx + ',' + (y + 14) + ')"><rect width="88" height="88" rx="' + ctlR +
    '" fill="' + TH.surface + '" stroke="' + TH.line + '"/>' +
    '<g transform="translate(8,8) scale(3)">' + logoSvg(TH, 24, true, true) + '</g></g>');
  s.node(txt(lx + 104, y + 32, 'at 16px:', { fs: 10, fill: TH.muted }));
  s.parts.push('<g transform="translate(' + (lx + 104) + ',' + (y + 40) + ') scale(0.67)">' +
    logoSvg(TH, 24, false, false) + '</g>');
  s.node(txt(lx + 128, y + 52, 'the mark holds', { fs: 10, fill: TH.faint }));
  s.parts.push('<g transform="translate(' + (lx + 216) + ',' + (y + 14) + ')"><rect width="32" height="32" rx="5" fill="' +
    TH.accent + '"/><g transform="translate(4,4) scale(1)"><svg width="24" height="24" viewBox="0 0 24 24"><g stroke="#ffffff" stroke-width="1.5">' +
    LOGO + '</g></svg></g></g>');
  s.node(txt(lx + 256, y + 28, 'app tile: reversed on accent', { fs: 10, fill: TH.muted }));
  s.node(txt(lx + 256, y + 42, 'wordmark 16/600, +0.01em,', { fs: 10, fill: TH.muted }));
  s.node(txt(lx + 256, y + 56, 'dropped under 720px', { fs: 10, fill: TH.muted }));
  y += 118;
  s.rule(y); y += 18;
  s.node(cap(MX, y, 'rejected', TH));
  s.rect(MX, y + 12, 470, 64, { fill: TH.surface, stroke: TH.line, rx: ctlR });
  s.parts.push('<g transform="translate(' + (MX + 16) + ',' + (y + 24) + ')"><rect width="24" height="24" rx="6" fill="#4b7bd8"/>' +
    '<circle cx="11" cy="11" r="6.5" fill="none" stroke="#fff" stroke-width="1.5"/><path d="M15.8 15.8 20 20" stroke="#fff" stroke-width="1.5" stroke-linecap="round"/></g>');
  s.node(txt(MX + 52, y + 38, 'a glyph inside a vendor-blue tile', { fs: 12, fill: TH.muted }));
  s.parts.push('<g transform="translate(' + (MX + 16) + ',' + (y + 50) + ')"><g stroke="' + TH.accent +
    '" stroke-width="3" fill="none" stroke-linecap="round"><path d="M4 7h16M4 12h16M4 17h16"/></g></g>');
  s.node(txt(MX + 52, y + 64, 'a 3px hamburger where a ⋯ belongs', { fs: 12, fill: TH.muted }));
  s.node(txt(MX + 262, y + 38, 'a tile is a second palette, a 24-grid icon', { fs: 11, fill: TH.muted }));
  s.node(txt(MX + 262, y + 56, 'degrades past 2px, and a glyph that needs', { fs: 11, fill: TH.muted }));
  s.node(txt(MX + 262, y + 72, 'a tooltip is not shipped.', { fs: 11, fill: TH.muted }));
  s.node(txt(620, y + 38, 'Universal verbs get icon-only controls, each with an aria-label:', { fs: 11, fill: TH.muted }));
  ['close', 'dots', 'plus', 'back', 'fwd', 'chev'].forEach((n, i) =>
    s.parts.push('<g transform="translate(' + (620 + i * 40) + ',' + (y + 48) + ')"><rect width="32" height="32" rx="' +
      ctlR + '" fill="' + TH.surface + '" stroke="' + TH.soft + '"/>' + I[n](8, 8, TH.ink).s + '</g>'));
  s.bottom = Math.max(s.bottom, y + 92);
  return s.done();
}

/* ============================ COMPONENTS ============================ */
function cardComp() {
  const s = new Sheet('Components — ghost-first, one tint, one shadow',
    'A border means frame, field, or error. Chips ride their content: 8px side padding, 20px tall. The same recipes run on both token sets.');
  const colW = [320, 320, 456];
  const xs = [MX, MX + 332, MX + 664];
  function block(th, x, w, top, caption) {
    s.node(cap(x, top, caption, th));
    const r = s.region(x, top + 12, w);
    r.th = th;
    r.row([btn(0, 0, 'Primary', th, { primary: true }), btn(0, 0, 'Quiet', th, { quiet: true }),
      btn(0, 0, 'Ghost', th, { strong: true })], 8);
    r.row([btn(0, 0, 'Copy', th, { icon: 'copy' }), btn(0, 0, 'Delete', th, { destruct: true, icon: 'trash' }),
      btn(0, 0, 'Locked', th, { icon: 'lock', disabled: true })], 6);
    r.row([chip(0, 0, 'Saved', th.muted, th), chip(0, 0, 'Saving…', th.rt, th),
      chip(0, 0, 'Not saved', th.danger, th, { danger: true })], 8);
    r.row([chip(0, 0, 'Saved', th.muted, th, { divider: 'Offline' }), pairHistory(th)], 8,
      'pill + joined pair');
    r.row([segmented(0, 0, 'Around', ['1 hop', '2 hops', 'All'], 1, th)], 8);
    r.say('selected segment: tint, plus a rule under its label', { fill: th.faint });
    r.row([field(0, 0, Math.min(224, w), th, { value: '' })], 0);
    r.say('field edge is a hint, the focus ring is the cue', { fill: th.faint });
    return r.bottom();
  }
  let top = 84;
  const b1 = block(TH, xs[0], colW[0], top, 'light');
  const b2 = block(TH, xs[1], colW[1], top, 'light');
  /* third column: menu + notices + island */
  {
    const x = xs[2], w = colW[2];
    s.node(cap(x, top, 'light', TH));
    const r = s.region(x, top + 12, w);
    r.th = TH;
    const menuW = 224, menuH = 168;
    r.put({ s: '<rect width="' + menuW + '" height="' + menuH + '" rx="' + ovR + '" fill="' + TH.raise +
      '" stroke="' + TH.line + '" filter="url(#sh)"/>' +
      '<text x="12" y="24" font-size="11" font-weight="500" letter-spacing="0.6" fill="' + TH.faint + '">SELECTION</text>' +
      '<rect x="8" y="34" width="' + (menuW - 16) + '" height="24" rx="' + ctlR + '" fill="' + TH.hover + '"/>' +
      '<text x="16" y="50" font-size="13" fill="' + TH.ink + '">Open</text><text x="' + (menuW - 16) + '" y="50" font-size="11" font-family="' + MONO + '" fill="' + TH.faint + '" text-anchor="end">Enter</text>' +
      '<text x="16" y="76" font-size="13" fill="' + TH.ink + '">Duplicate</text><text x="' + (menuW - 16) + '" y="76" font-size="11" font-family="' + MONO + '" fill="' + TH.faint + '" text-anchor="end">Ctrl+D</text>' +
      '<text x="16" y="102" font-size="13" fill="' + TH.ink + '">Copy as ▸ PNG</text>' +
      '<text x="16" y="128" font-size="13" fill="' + TH.faint + '">Connect</text>' +
      '<line x1="8" y1="136" x2="' + (menuW - 8) + '" y2="136" stroke="' + TH.soft + '"/>' +
      '<text x="16" y="158" font-size="13" fill="' + TH.danger + '">Delete</text><text x="' + (menuW - 16) + '" y="158" font-size="11" font-family="' + MONO + '" fill="' + TH.faint + '" text-anchor="end">Del</text>',
      w: menuW, h: menuH }, 6);
    r.say('opens clear of its target, hover fills, destructive last', { fill: TH.faint });
    r.put({ s: '<rect width="224" height="28" rx="' + ovR + '" fill="' + TH.raise + '" stroke="' + TH.soft +
      '" filter="url(#sh)"/><text x="12" y="18" font-size="13" fill="' + TH.ink + '">Deleted 3 items · <tspan fill="' +
      TH.tx + '">Undo</tspan></text>', w: 224, h: 28 }, 6);
    r.put({ s: '<rect width="224" height="28" rx="' + ovR + '" fill="' + TH.raise + '" stroke="' + TH.danger +
      '" filter="url(#sh)"/><text x="12" y="18" font-size="13" fill="' + TH.danger +
      '">Not saved — disk full · <tspan text-decoration="underline">Retry</tspan></text>', w: 224, h: 28 }, 6);
    r.say('a shadow carries a temporal surface, a danger border carries bad news', { fill: TH.faint });
    top = Math.max(b1, b2, r.bottom());
  }
  top += 14;
  s.rule(top); top += 20;
  const bandTop = top - 10;
  const d1 = block(DK, xs[0], colW[0], top, 'dark');
  const d2 = block(DK, xs[1], colW[1], top, 'dark');
  {
    const x = xs[2];
    s.node(cap(x, top, 'dark', DK));
    const r = s.region(x, top + 12, colW[2]);
    r.th = DK;
    r.put({ s: '<rect width="' + colW[2] + '" height="44" fill="' + DK.surface + '"/>' +
      '<line x1="0" y1="0" x2="' + colW[2] + '" y2="0" stroke="' + DK.line + '"/>' +
      '<g transform="translate(12,8)">' + field(0, 0, 252, DK, { value: '' }).s +
      '<text x="10" y="19" font-size="14" fill="' + DK.faint + '">Write a thought…</text></g>' +
      '<g transform="translate(276,8)">' + btn(0, 0, 'Add', DK, { primary: true, w: 72 }).s + '</g>',
      w: colW[2], h: 44 }, 4);
    r.say('the strip edge is the frame (--line); its field floats (--line-soft)', { fill: DK.faint });
    r.put({ s: '<rect width="280" height="40" rx="' + ctlR + '" fill="' + DK.work + '" stroke="' + DK.line + '"/>' +
      '<rect x="20" y="8" width="220" height="24" rx="' + ctlR + '" fill="' + DK.raise + '" filter="url(#sh)"/>' +
      '<rect x="20" y="8" width="3" height="24" fill="' + DK.rt + '"/>' +
      '<text x="32" y="25" font-size="14" fill="' + DK.ink + '">a title, typed here</text>', w: 280, h: 40 }, 4);
    r.say('an island separates with a 3px accent stripe, not a frame', { fill: DK.faint });
    top = Math.max(d1, d2, r.bottom());
  }
  s.parts.splice(2, 0, '<rect x="' + (MX - 12) + '" y="' + bandTop + '" width="' + (W - 2 * MX + 24) +
    '" height="' + (top - bandTop + 12) + '" rx="' + ovR + '" fill="' + DK.bg + '"/>');
  s.bottom = Math.max(s.bottom, top + 12);
  return s.done();
}

/* ============================ NAVIGATION: ONE FRAME ============================ */
function cardNav() {
  const s = new Sheet('One frame — chrome geometry never moves between views',
    'Header 44px with six controls, a fixed-width band, published slot heights. Frame drift reads as a broken app.');
  const FX = MX, FY = 84, FW = W - 2 * MX, FH = 392;
  s.rect(FX, FY, FW, FH, { fill: TH.surface, stroke: TH.line, rx: ovR });
  s.parts.push('<clipPath id="frame"><rect x="' + FX + '" y="' + FY + '" width="' + FW + '" height="' + FH +
    '" rx="' + ovR + '"/></clipPath><g clip-path="url(#frame)">');
  /* header */
  s.parts.push('<rect x="' + FX + '" y="' + FY + '" width="' + FW + '" height="44" fill="' + TH.surface + '"/>' +
    '<line x1="' + FX + '" y1="' + (FY + 44) + '" x2="' + (FX + FW) + '" y2="' + (FY + 44) + '" stroke="' + TH.line + '"/>');
  s.parts.push('<g transform="translate(' + (FX + 12) + ',' + (FY + 12) + ') scale(0.833)">' +
    logoSvg(TH, 24, true, false) + '</g>');
  s.node(txt(FX + 40, FY + 28, 'mind', { fs: 16, w: 600, fill: TH.ink }));
  let nx = FX + 96;
  for (const [i, l] of ['Library', 'Graph', 'Settings'].entries()) {
    const n = txt(nx, FY + 27, l, { fs: 14, w: i === 0 ? 500 : 400, fill: i === 0 ? TH.ink : TH.muted });
    s.node(n);
    if (i === 0) s.parts.push('<rect x="' + nx + '" y="' + (FY + 34) + '" width="' + (n.w - 8) + '" height="2" fill="' + TH.rt + '"/>');
    nx += n.w + 24;
  }
  const bandW = 320, bandX = nx + 12;
  s.parts.push('<rect x="' + bandX + '" y="' + (FY + 4) + '" width="' + bandW + '" height="36" fill="' + TH.raise + '"/>');
  s.node(txt(bandX + 8, FY + 26, 'band: fixed width, painted once', { fs: 11, fill: TH.faint }));
  let rx = FX + FW - 12;
  s.parts.push(I.dots(rx - 16, FY + 14, TH.muted).s); rx -= 40;
  const ch = chip(0, 0, 'Saved', TH.muted, TH, { divider: 'Offline' });
  s.parts.push('<g transform="translate(' + (rx - ch.w) + ',' + (FY + 10) + ')">' + ch.s + '</g>'); rx -= ch.w + 16;
  s.parts.push('<g transform="translate(' + (rx - 76) + ',' + (FY + 8) + ')">' + pairHistory(TH).s + '</g>'); rx -= 92;
  s.parts.push('<g transform="translate(' + (rx - 100) + ',' + (FY + 8) + ')">' + btn(0, 0, 'Capture', TH, { primary: true, w: 100 }).s + '</g>');
  rx -= 108;
  s.parts.push(I.search(rx - 16, FY + 14, TH.ink).s);
  s.parts.push('</g>');
  s.node(txt(FX + FW - 12, FY + 62, 'six controls, a cap', { fs: 10, fill: TH.faint, anchor: 'end' }));
  /* breadcrumb */
  s.node(txt(FX + 16, FY + 66, 'Library', { fs: 13, fill: TH.muted }));
  s.node(txt(FX + 70, FY + 66, '/', { fs: 13, fill: TH.faint }));
  s.node(txt(FX + 80, FY + 66, 'Field notes', { fs: 13, fill: TH.muted }));
  s.node(txt(FX + 168, FY + 66, '/', { fs: 13, fill: TH.faint }));
  s.node(txt(FX + 178, FY + 66, 'Atom', { fs: 13, fill: TH.ink }));
  /* table */
  const TY = FY + 82, TW = FW - 252;
  const heads = [['NAME', FX + 24], ['OWNER', FX + 244], ['UPDATED', FX + 356], ['STATUS', FX + 488]];
  heads.forEach((h) => s.node(txt(h[1], TY + 12, h[0], { fs: 11, w: 500, ls: 0.5, caps: true, fill: TH.faint })));
  const rowsY = TY + 22;
  for (let i = 0; i < 7; i++) {
    const y = rowsY + i * 30;
    if (i === 2) s.parts.push('<rect x="' + (FX + 16) + '" y="' + y + '" width="' + (TW - 8) + '" height="30" fill="' +
      TH.tint + '"/>');
    else s.parts.push('<line x1="' + (FX + 16) + '" y1="' + (y + 30) + '" x2="' + (FX + TW - 8) + '" y2="' +
      (y + 30) + '" stroke="' + TH.soft + '"/>');
    s.node(txt(FX + 24, y + 19, 'Determinism notes ' + (i + 1), { fs: 13, fill: TH.ink }));
    s.node(txt(FX + 244, y + 19, ['km', 'ra', 'jd', 'km', 'tt', 'ra', 'jd'][i], { fs: 13, fill: TH.muted }));
    s.node(txt(FX + 356, y + 19, '2026-11-1' + i, { fs: 12, mono: true, fill: TH.muted }));
    s.node(txt(FX + 488, y + 19, i < 2 ? 'review' : i < 5 ? 'live' : 'draft', { fs: 12, fill: TH.muted }));
    s.parts.push(I[i === 2 ? 'pencil' : 'dots'](FX + TW - 32, y + 7, i === 2 ? TH.muted : TH.faint).s);
  }
  /* bulk bar */
  const BY = rowsY + 7 * 30 + 8;
  s.parts.push('<rect x="' + (FX + 16) + '" y="' + BY + '" width="248" height="32" rx="' + ovR + '" fill="' +
    TH.raise + '" stroke="' + TH.line + '" filter="url(#sh)"/>');
  s.node(txt(FX + 28, BY + 20, '3 selected', { fs: 13, w: 500, fill: TH.ink }));
  s.parts.push('<g transform="translate(' + (FX + 116) + ',' + (BY + 2) + ')">' + btn(0, 0, 'Tag', TH, { strong: true }).s + '</g>');
  s.parts.push('<g transform="translate(' + (FX + 168) + ',' + (BY + 2) + ')">' + btn(0, 0, 'Export', TH, {}).s + '</g>');
  s.parts.push(I.close(FX + 240, BY + 8, TH.muted).s);
  s.node(txt(FX + 284, BY + 20, 'tier 1, only with a selection, at a fixed edge', { fs: 10, fill: TH.faint }));
  /* inspector */
  const IX = FX + FW - 232, IY = FY + 56;
  s.parts.push('<rect x="' + IX + '" y="' + IY + '" width="216" height="220" rx="' + ovR + '" fill="' + TH.raise +
    '" stroke="' + TH.line + '" filter="url(#sh)"/>');
  s.node(txt(IX + 12, IY + 22, 'Atom', { fs: 16, w: 500, fill: TH.ink }));
  s.parts.push(I.close(IX + 192, IY + 8, TH.muted).s);
  s.node(txt(IX + 12, IY + 40, 'atom-7f3c19', { fs: 11, mono: true, fill: TH.faint }));
  s.parts.push('<line x1="' + (IX + 8) + '" y1="' + (IY + 52) + '" x2="' + (IX + 208) + '" y2="' + (IY + 52) +
    '" stroke="' + TH.soft + '"/>');
  s.node(cap(IX + 12, IY + 70, 'links', TH));
  ['Determinism notes', 'Zoom research', 'Semantic zoom'].forEach((l, i) => {
    s.node(txt(IX + 12, IY + 90 + i * 22, l, { fs: 12, fill: TH.tx }));
    s.parts.push(I.link(IX + 192, IY + 78 + i * 22, TH.faint).s);
  });
  s.parts.push('<line x1="' + (IX + 8) + '" y1="' + (IY + 162) + '" x2="' + (IX + 208) + '" y2="' + (IY + 162) +
    '" stroke="' + TH.soft + '"/>');
  s.node(cap(IX + 12, IY + 180, 'meta', TH));
  s.node(txt(IX + 12, IY + 198, 'created 2026-11-02', { fs: 11, mono: true, fill: TH.muted }));
  s.node(txt(IX + 12, IY + 212, 'edited 2 minutes ago', { fs: 11, mono: true, fill: TH.muted }));
  s.node(txt(IX, IY + 236, 'inspector: 100ms fade, no scrim', { fs: 10, fill: TH.faint }));
  /* tiers + z ladder below the frame */
  let y = FY + FH + 24;
  s.node(cap(MX, y, 'disclosure tiers: no action lives twice, all four view one registry id', TH));
  const tiers = [['0', 'header, always', 'nav search capture pair chip ⋯', '6 max'],
    ['1', 'contextual bar', '2–4 verbs for the selection', '4 + ⋯'],
    ['2', 'context menu', 'every action for that target', '9 · 2 sub'],
    ['3', 'Ctrl+K', 'everything typed, > commands', '1 input']];
  tiers.forEach((t2, i) => {
    const x = MX + i * 158;
    s.rect(x, y + 12, 150, 60, { fill: TH.surface, stroke: TH.line, rx: ctlR });
    s.node(txt(x + 10, y + 30, 'tier ' + t2[0], { fs: 12, w: 600, mono: true, fill: TH.tx }));
    s.node(txt(x + 10, y + 45, t2[1], { fs: 12, w: 500, fill: TH.ink }));
    s.node(txt(x + 10, y + 60, t2[2], { fs: 9, fill: TH.muted }));
    s.node(txt(x + 140, y + 30, t2[3], { fs: 9, mono: true, fill: TH.faint, anchor: 'end' }));
  });
  s.node(cap(MX + 668, y, 'z ladder, fixed integers', TH));
  const zl = ['0 surface', '5 strips', '6 members', '7 menus', '20 island', '30 notice', '40 inspector',
    '45 capture', '50 modal', '60 ctx menu'];
  zl.forEach((z, i) => {
    const x = MX + 668 + (i % 5) * 100, yy = y + 26 + Math.floor(i / 5) * 18;
    s.node(txt(x, yy, z, { fs: 10, mono: true, fill: TH.muted }));
  });
  s.node(txt(MX + 668, y + 62, 'a frame member outranks every transient', { fs: 10, fill: TH.faint }));
  y += 96;
  s.node(txt(MX, y, 'Compact ≤720px: drop the wordmark, hints, and chip words. Tap targets reach 44px on an invisible pseudo-box, so the painted layout never moves.',
    { fs: 11, fill: TH.muted }));
  y += 18;
  s.node(txt(MX, y, 'Density is a dial, not a default: 30px data rows, 44px touch rows, 76px of chrome total. On a 900px viewport, 88% holds content.',
    { fs: 11, fill: TH.muted }));
  s.bottom = Math.max(s.bottom, y + 8);
  return s.done();
}

/* ============================ MOTION ============================ */
function cardMotion() {
  const s = new Sheet('Motion — two properties, three durations, one curve',
    'opacity and transform only. Hover and focus are instant: a control that must feel immediate cannot spend 150ms saying so.');
  let y = 92;
  const bars = [['fade', 100, 'menu · popover · modal · notice · inspector · bulk bar', TH.rt],
    ['exit', 120, 'every exit · detail-level crossfade · result swap', TH.muted],
    ['move', 180, 'FLIP re-layout · route crossfade · drawer in · ornament', TH.ink]];
  for (const r of bars) {
    s.node(txt(MX, y + 12, r[0], { fs: 13, w: 500, fill: TH.ink }));
    s.parts.push('<rect x="' + (MX + 72) + '" y="' + y + '" width="' + r[1] + '" height="16" rx="2" fill="' + r[3] + '"/>');
    s.node(txt(MX + 88 + r[1], y + 12, r[1] + 'ms — ' + r[2], { fs: 12, fill: TH.muted }));
    y += 32;
  }
  s.node(txt(MX, y + 6, '1px = 1ms. Under 100ms reads as instant, past 180ms delays the next input.',
    { fs: 11, fill: TH.faint }));
  const ax = 596, aw = 200, ah = 140, ay = 88;
  s.node(cap(ax, ay - 8, 'the only easing: cubic-bezier(0.2, 0, 0, 1)', TH));
  s.rect(ax, ay, aw, ah, { fill: TH.surface, stroke: TH.line });
  for (let i = 1; i < 4; i++) {
    s.parts.push('<line x1="' + ax + '" y1="' + (ay + (ah / 4) * i) + '" x2="' + (ax + aw) + '" y2="' +
      (ay + (ah / 4) * i) + '" stroke="' + TH.soft + '"/><line x1="' + (ax + (aw / 4) * i) + '" y1="' + ay +
      '" x2="' + (ax + (aw / 4) * i) + '" y2="' + (ay + ah) + '" stroke="' + TH.soft + '"/>');
  }
  s.parts.push('<path d="M' + ax + ' ' + (ay + ah) + ' C' + (ax + 0.2 * aw) + ' ' + (ay + ah) + ' ' + ax + ' ' +
    ay + ' ' + (ax + aw) + ' ' + ay + '" fill="none" stroke="' + TH.rt + '" stroke-width="2"/>');
  s.parts.push('<line x1="' + ax + '" y1="' + (ay + ah) + '" x2="' + (ax + aw) + '" y2="' + ay +
    '" stroke="' + TH.line + '" stroke-dasharray="3 3"/>');
  s.node(txt(ax, ay + ah + 16, 'time →', { fs: 10, mono: true, fill: TH.faint }));
  s.node(txt(ax, ay + ah + 34, 'Fast start, long settle. No overshoot:', { fs: 11, fill: TH.muted }));
  s.node(txt(ax, ay + ah + 50, 'a UI that wobbles reads as a toy.', { fs: 11, fill: TH.muted }));
  /* dark: reduced motion, ornament, budget */
  const dTop = 300;
  s.rect(584, dTop, RIGHT - 584 + 12, 208, { fill: DK.bg, rx: ovR });
  let d = dTop + 28;
  s.node(cap(600, d, 'reduced motion is a requirement', DK)); d += 24;
  s.node(txt(600, d, '@media (prefers-reduced-motion: reduce)', { fs: 12, mono: true, fill: DK.tx })); d += 18;
  s.node(txt(600, d, '{ * { animation: none; transition: none; } }', { fs: 12, mono: true, fill: DK.ink })); d += 24;
  s.node(txt(600, d, 'State stays correct, motion is absent, a backdrop', { fs: 12, fill: DK.muted })); d += 18;
  s.node(txt(600, d, 'paints one frame.', { fs: 12, fill: DK.muted })); d += 28;
  s.node(cap(600, d, 'the one ornament + the budget', DK)); d += 22;
  const lw2 = txt(600, d, 'Library', { fs: 14, w: 500, fill: DK.ink });
  s.node(lw2);
  s.parts.push('<rect x="600" y="' + (d + 8) + '" width="' + (lw2.w - 8) + '" height="2" fill="' + DK.rt + '"/>');
  s.node(txt(700, d, 'scaleX(0.4→1) + fade, 180ms, once', { fs: 11, fill: DK.muted }));
  s.node(txt(600, d + 28, 'will-change only on what animates · never width, height, top, left ·', { fs: 11, fill: DK.muted }));
  s.node(txt(600, d + 44, 'holds 55fps on a mid-tier laptop, or the motion is cut', { fs: 11, fill: DK.muted }));
  /* FLIP */
  y = 220;
  s.node(cap(MX, y, 'FLIP: the focused row keeps its position, the container moves', TH));
  const before = ['row a', 'row b', 'focused', 'row c'];
  const after = ['focused', 'row a', 'row b', 'row c'];
  const flip = (x0, items) => {
    s.rect(x0, y + 12, 228, 136, { fill: TH.surface, stroke: TH.line, rx: ctlR });
    items.forEach((l, i) => {
      const yy = y + 16 + i * 32, sel = l === 'focused';
      if (sel) s.parts.push('<rect x="' + (x0 + 4) + '" y="' + yy + '" width="220" height="28" fill="' + TH.tint + '"/>');
      if (i === 3 && items === after) s.parts.push('<rect x="' + (x0 + 4) + '" y="' + yy + '" width="220" height="28" rx="2" fill="none" stroke="' + TH.soft + '" stroke-dasharray="4 3"/>');
      s.node(txt(x0 + 16, yy + 18, l, { fs: 13, w: sel ? 500 : 400, fill: sel ? TH.ink : TH.muted }));
    });
  };
  flip(MX, before);
  s.parts.push('<path d="M' + (MX + 240) + ' ' + (y + 78) + ' h40" stroke="' + TH.muted + '" stroke-dasharray="4 3"/>' +
    '<path d="m' + (MX + 272) + ' ' + (y + 74) + ' 8 4-8 4z" fill="' + TH.muted + '"/>');
  flip(MX + 292, after);
  s.node(txt(MX, y + 164, 'measure → reorder → interpolate to identity', { fs: 10, fill: TH.faint }));
  s.node(txt(MX + 292, y + 164, 'a new row fades + scales 0.96→1, ≤12ms stagger, 120ms cap', { fs: 10, fill: TH.faint }));
  /* slides vs fades */
  y = 432;
  s.node(cap(MX, y, 'slides versus fades', TH));
  s.rect(MX, y + 12, 268, 96, { fill: TH.surface, stroke: TH.line, rx: ctlR });
  s.node(txt(MX + 12, y + 34, 'FADE, 100 in / 120 out', { fs: 13, w: 500, fill: TH.ink }));
  s.node(txt(MX + 12, y + 54, 'menu · popover · modal · notice ·', { fs: 12, fill: TH.muted }));
  s.node(txt(MX + 12, y + 72, 'inspector · bulk bar · palette', { fs: 12, fill: TH.muted }));
  s.node(txt(MX + 12, y + 94, 'a route change crossfades at 180ms', { fs: 10, w: 500, fill: TH.ink }));
  s.rect(MX + 288, y + 12, 268, 96, { fill: TH.surface, stroke: TH.line, rx: ctlR });
  s.node(txt(MX + 300, y + 34, 'SLIDE 180 in / 120 out', { fs: 13, w: 500, fill: TH.ink }));
  s.node(txt(MX + 300, y + 54, 'drawer ≤400px · sheet ≤70vh, over a', { fs: 12, fill: TH.muted }));
  s.node(txt(MX + 300, y + 72, '28% scrim, focus trapped, scroll locked', { fs: 12, fill: TH.muted }));
  s.node(txt(MX + 300, y + 94, 'a slide claims spatial movement; a fade does not', { fs: 10, w: 500, fill: TH.ink }));
  s.node(txt(MX, y + 128, 'Skeletons are static and shaped like the incoming content. Anything that resolves under 200ms shows nothing at all.',
    { fs: 11, fill: TH.muted }));
  s.bottom = Math.max(s.bottom, dTop + 208, y + 136);
  return s.done();
}

/* ---------- emit + audit ---------- */
const files = {
  'palette.svg': cardPalette(), 'typography.svg': cardType(), 'iconography.svg': cardIcons(),
  'components.svg': cardComp(), 'navigation.svg': cardNav(), 'motion.svg': cardMotion(),
};
for (const f of Object.keys(files)) fs.writeFileSync(__dirname + '/' + f, files[f]);
const table = [];
for (const t of [TH, DK]) {
  table.push([t.name, 'ink ' + cr(t.ink, t.surface), 'muted ' + cr(t.muted, t.surface),
    'faint ' + cr(t.faint, t.surface), 'accent-text ' + cr(t.tx, t.surface), 'on ' + cr(t.on, t.accent),
    'danger ' + cr(t.dtx, t.surface), 'ink-on-tint ' + cr(t.ink, t.tint)]);
}
console.table(table);
for (const f of Object.keys(files)) {
  const vb = files[f].match(/viewBox="0 0 (\d+) (\d+)"/);
  console.log(f.padEnd(18), vb[1] + 'x' + vb[2]);
}
