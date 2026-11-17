import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, "..");
const THEMES_BASE_DIR = path.join(ROOT, ".omp", "evidence", "theme");
const COPPER_DIR = path.join(THEMES_BASE_DIR, "copper");

if (!fs.existsSync(COPPER_DIR)) {
  fs.mkdirSync(COPPER_DIR, { recursive: true });
}

// Crisp SVG Icons (Strictly Vector, No Blurs/Hazy Filters)
const icons = {
  logo: (accent) =>
    `<svg viewBox="0 0 24 24" class="w-6 h-6" fill="none" stroke="currentColor" stroke-width="2.2"><path d="M12 2L2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5" stroke="${accent}"/></svg>`,
  works: `<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="7"></rect><rect x="14" y="3" width="7" height="7"></rect><rect x="14" y="14" width="7" height="7"></rect><rect x="3" y="14" width="7" height="7"></rect></svg>`,
  flows: `<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="6" y1="3" x2="6" y2="15"></line><circle cx="18" cy="6" r="3"></circle><circle cx="6" cy="18" r="3"></circle><path d="M18 9a9 9 0 0 1-9 9"></path></svg>`,
  monitor: `<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="22 12 18 12 15 21 9 3 6 12 2 12"></polyline></svg>`,
  team: `<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"></path><circle cx="9" cy="7" r="4"></circle><path d="M23 21v-2a4 4 0 0 0-3-3.87"></path><path d="M16 3.13a4 4 0 0 1 0 7.75"></path></svg>`,
  config: `<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"></circle><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z"></path></svg>`,
  aideck: `<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m12 3-1.9 5.8a2 2 0 0 1-1.3 1.3L3 12l5.8 1.9a2 2 0 0 1 1.3 1.3L12 21l1.9-5.8a2 2 0 0 1 1.3-1.3L21 12l-5.8-1.9a2 2 0 0 1-1.3-1.3Z"></path></svg>`,
  zap: `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2"></polygon></svg>`,
  play: `<svg viewBox="0 0 24 24" width="13" height="13" fill="currentColor" stroke="none"><polygon points="5 3 19 12 5 21 5 3"></polygon></svg>`,
  save: `<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"></path><polyline points="17 21 17 13 7 13 7 21"></polyline><polyline points="7 3 7 8 15 8"></polyline></svg>`,
  check: `<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"></polyline></svg>`,
  link: `<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"></path><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"></path></svg>`,
  shield: `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"></path></svg>`,
  bot: `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="10" rx="2"></rect><circle cx="12" cy="5" r="2"></circle><path d="M12 7v4"></path><line x1="8" y1="16" x2="8" y2="16"></line><line x1="16" y1="16" x2="16" y2="16"></line></svg>`,
  search: `<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="8"></circle><line x1="21" y1="21" x2="16.65" y2="16.65"></line></svg>`,
  bell: `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"></path><path d="M13.73 21a2 2 0 0 1-3.46 0"></path></svg>`,
  sun: `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="5"></circle><line x1="12" y1="1" x2="12" y2="3"></line><line x1="12" y1="21" x2="12" y2="23"></line><line x1="4.22" y1="4.22" x2="5.64" y2="5.64"></line><line x1="18.36" y1="18.36" x2="19.78" y2="19.78"></line><line x1="1" y1="12" x2="3" y2="12"></line><line x1="21" y1="12" x2="23" y2="12"></line><line x1="4.22" y1="19.78" x2="5.64" y2="18.36"></line><line x1="18.36" y1="5.64" x2="19.78" y2="4.22"></line></svg>`,
  moon: `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"></path></svg>`,
  filter: `<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3"></polygon></svg>`,
  cpu: `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="4" width="16" height="16" rx="2"></rect><rect x="9" y="9" width="6" height="6"></rect><line x1="9" y1="1" x2="9" y2="4"></line><line x1="15" y1="1" x2="15" y2="4"></line><line x1="9" y1="20" x2="9" y2="23"></line><line x1="15" y1="20" x2="15" y2="23"></line><line x1="20" y1="9" x2="23" y2="9"></line><line x1="20" y1="14" x2="23" y2="14"></line><line x1="1" y1="9" x2="4" y2="9"></line><line x1="1" y1="14" x2="4" y2="14"></line></svg>`,
  gitCommit: `<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="4"></circle><line x1="1.05" y1="12" x2="7" y2="12"></line><line x1="17.01" y1="12" x2="22.96" y2="12"></line></svg>`,
};

// Refined Copper Design System
export const THEME = {
  id: "copper",
  name: "Copper & Teal",
  title: "Open Facilitator — Refined Copper Theme",
  subtitle:
    "Industrial precision copper (#C96A2D) and rich dark teal (#168B90) aesthetic with crisp surface hierarchy, solid opaque token palettes, and cool technical grounds",
  badgeText: "REFINED COPPER & TEAL DESIGN SYSTEM",
  dark: {
    bg: "#232831",
    surface: "#2B313B",
    surfaceAlt: "#323944",
    surfaceHighlight: "#3B4350",
    border: "#3F4754",
    borderSubtle: "#343B47",
    ink: "#F3F4F6",
    inkMuted: "#9CA3AF",
    inkFaint: "#6B7280",
    primary: "#C96A2D",
    primaryHover: "#E58A45",
    primaryText: "#E58A45",
    primaryTint: "#3D332D",
    primaryBorder: "#8A4820",
    info: "#168B90",
    infoHover: "#1DA4A9",
    infoTint: "#223538",
    infoBorder: "#175B5F",
    success: "#58894C",
    successTint: "#273426",
    successBorder: "#3B5832",
    warning: "#C48A00",
    warningTint: "#373121",
    warningBorder: "#7A5B0B",
    error: "#C75454",
    errorTint: "#382626",
    errorBorder: "#7D2E2E",
    gridDot: "rgba(255, 255, 255, 0.05)",
  },
  light: {
    bg: "#F1F3F5",
    surface: "#FFFFFF",
    surfaceAlt: "#E8ECF0",
    surfaceHighlight: "#DFE4EA",
    border: "#CFD6E0",
    borderSubtle: "#DEE3EB",
    ink: "#1A202C",
    inkMuted: "#505A69",
    inkFaint: "#7A8699",
    primary: "#C96A2D",
    primaryHover: "#B3581E",
    primaryText: "#AC531C",
    primaryTint: "#F8EFE8",
    primaryBorder: "#E2B696",
    info: "#127377",
    infoHover: "#0E5C5F",
    infoTint: "#E5F3F4",
    infoBorder: "#92CAD0",
    success: "#447A38",
    successTint: "#EAF3E8",
    successBorder: "#9FC497",
    warning: "#A87400",
    warningTint: "#FAF2DE",
    warningBorder: "#DEC177",
    error: "#BA3C3C",
    errorTint: "#F9E8E8",
    errorBorder: "#DF9E9E",
    gridDot: "rgba(0, 0, 0, 0.04)",
  },
};

function generateFullUI(themeConfig, mode = "dark") {
  const isDark = mode === "dark";
  const tokens = isDark ? themeConfig.dark : themeConfig.light;

  const bg = tokens.bg;
  const surface = tokens.surface;
  const surfaceAlt = tokens.surfaceAlt;
  const border = tokens.border;
  const borderSubtle = tokens.borderSubtle;

  const ink = tokens.ink;
  const inkMuted = tokens.inkMuted;
  const inkFaint = tokens.inkFaint;

  const primary = tokens.primary;
  const primaryHover = tokens.primaryHover;
  const primaryText = tokens.primaryText;
  const primaryTint = tokens.primaryTint;
  const primaryBorder = tokens.primaryBorder;

  const info = tokens.info;
  const infoTint = tokens.infoTint;
  const infoBorder = tokens.infoBorder;

  const success = tokens.success;
  const successTint = tokens.successTint;
  const successBorder = tokens.successBorder;

  const gridDot = tokens.gridDot;

  return `
    <div class="ui-container ${mode}-mode" style="
      background-color: ${bg};
      color: ${ink};
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
      width: 100%;
      height: 100%;
      display: flex;
      flex-direction: column;
      overflow: hidden;
      box-sizing: border-box;
      position: relative;
    ">
      <!-- 1. Top Navigation Header -->
      <header style="
        height: 52px;
        background: ${surface};
        border-bottom: 1px solid ${border};
        display: flex;
        align-items: center;
        justify-content: space-between;
        padding: 0 18px;
        flex-shrink: 0;
        z-index: 20;
      ">
        <!-- Logo & Environment Switcher -->
        <div style="display: flex; align-items: center; gap: 14px;">
          <div style="display: flex; align-items: center; gap: 8px;">
            <div style="
              width: 30px;
              height: 30px;
              background: ${primary};
              border-radius: 7px;
              display: flex;
              align-items: center;
              justify-content: center;
              box-shadow: 0 1px 3px rgba(0,0,0,0.18);
            ">
              <svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="#FFFFFF" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round">
                <polygon points="12 2 2 7 12 12 22 7 12 2"></polygon>
                <polyline points="2 17 12 22 22 17"></polyline>
                <polyline points="2 12 12 17 22 12"></polyline>
              </svg>
            </div>
            <div>
              <div style="font-size: 13px; font-weight: 800; letter-spacing: 0.5px; line-height: 1.1; color: ${ink};">
                OPEN FACILITATOR
              </div>
              <div style="font-size: 10px; color: ${inkMuted}; font-weight: 500;">
                Autonomous Orchestration Kernel
              </div>
            </div>
          </div>

          <div style="height: 18px; width: 1px; background: ${border};"></div>

          <!-- Environment Badge -->
          <div style="
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 3px 8px;
            background: ${surfaceAlt};
            border: 1px solid ${border};
            border-radius: 5px;
            font-size: 11px;
            font-family: ui-monospace, Menlo, monospace;
            color: ${inkMuted};
          ">
            <span style="width: 7px; height: 7px; border-radius: 50%; background: ${info};"></span>
            <span style="font-weight: 600; color: ${ink};">us-east-prod</span>
            <span style="color: ${inkFaint};">·</span>
            <span style="color: ${info};">18 Gateways</span>
          </div>
        </div>

        <!-- Center Navigation Tabs with Primary Active States -->
        <nav style="display: flex; align-items: center; gap: 4px; height: 100%;">
          <div style="
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 6px 12px;
            font-size: 12.5px;
            font-weight: 500;
            color: ${inkMuted};
            border-radius: 6px;
            cursor: pointer;
          ">
            ${icons.works}
            <span>Works</span>
            <span style="
              padding: 1px 6px;
              background: ${surfaceAlt};
              color: ${inkMuted};
              border-radius: 10px;
              font-size: 10px;
              font-weight: 700;
            ">14</span>
          </div>

          <!-- Active Flows Tab with Theme Primary Indicator -->
          <div style="
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 6px 14px;
            font-size: 12.5px;
            font-weight: 600;
            color: ${isDark ? primaryHover : primaryText};
            background: ${primaryTint};
            border: 1px solid ${primaryBorder};
            border-radius: 6px;
            cursor: pointer;
          ">
            <div style="color: ${primary}; display: flex; align-items: center;">${icons.flows}</div>
            <span>Flows</span>
            <span style="
              width: 5px;
              height: 5px;
              border-radius: 50%;
              background: ${primary};
            "></span>
          </div>

          <div style="
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 6px 12px;
            font-size: 12.5px;
            font-weight: 500;
            color: ${inkMuted};
            border-radius: 6px;
          ">
            ${icons.monitor}
            <span>Deliveries</span>
          </div>

          <div style="
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 6px 12px;
            font-size: 12.5px;
            font-weight: 500;
            color: ${inkMuted};
            border-radius: 6px;
          ">
            ${icons.shield}
            <span>Audit</span>
          </div>

          <div style="
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 6px 12px;
            font-size: 12.5px;
            font-weight: 500;
            color: ${inkMuted};
            border-radius: 6px;
          ">
            ${icons.link}
            <span>Link Hub</span>
          </div>

          <div style="
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 6px 12px;
            font-size: 12.5px;
            font-weight: 500;
            color: ${inkMuted};
            border-radius: 6px;
          ">
            ${icons.config}
            <span>Config</span>
          </div>

          <!-- AiDeck Drawer Tab in Secondary Info Accent -->
          <div style="
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 6px 12px;
            font-size: 12.5px;
            font-weight: 500;
            color: ${info};
            background: ${infoTint};
            border: 1px solid ${infoBorder};
            border-radius: 6px;
          ">
            ${icons.aideck}
            <span>AiDeck</span>
          </div>
        </nav>

        <!-- Right Quick Actions & Search -->
        <div style="display: flex; align-items: center; gap: 10px;">
          <!-- Search Bar -->
          <div style="
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 5px 10px;
            background: ${surfaceAlt};
            border: 1px solid ${border};
            border-radius: 6px;
            font-size: 11.5px;
            color: ${inkMuted};
            width: 170px;
          ">
            ${icons.search}
            <span>⌘K Search flows...</span>
          </div>

          <!-- Outbound Links Pill (Info State) -->
          <div style="
            display: flex;
            align-items: center;
            gap: 5px;
            padding: 4px 9px;
            background: ${infoTint};
            border: 1px solid ${infoBorder};
            border-radius: 6px;
            font-size: 11px;
            font-weight: 600;
            color: ${info};
          ">
            ${icons.link}
            <span>Link Hub: 18 Gateways</span>
          </div>

          <!-- Theme & Bell -->
          <div style="
            width: 28px;
            height: 28px;
            border-radius: 6px;
            background: ${surfaceAlt};
            border: 1px solid ${border};
            display: flex;
            align-items: center;
            justify-content: center;
            color: ${inkMuted};
          ">
            ${isDark ? icons.sun : icons.moon}
          </div>

          <div style="
            width: 28px;
            height: 28px;
            border-radius: 6px;
            background: ${surfaceAlt};
            border: 1px solid ${border};
            display: flex;
            align-items: center;
            justify-content: center;
            color: ${inkMuted};
            position: relative;
          ">
            ${icons.bell}
            <span style="position: absolute; top: 4px; right: 4px; width: 6px; height: 6px; border-radius: 50%; background: ${primary};"></span>
          </div>

          <!-- User Avatar -->
          <div style="
            width: 28px;
            height: 28px;
            border-radius: 50%;
            background: ${primary};
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 11px;
            font-weight: 700;
            color: #FFFFFF;
            border: 1px solid ${border};
          ">
            OF
          </div>
        </div>
      </header>

      <!-- 2. Sub-Header: Active Workflow Meta & Action Bar -->
      <div style="
        height: 46px;
        background: ${surfaceAlt};
        border-bottom: 1px solid ${border};
        display: flex;
        align-items: center;
        justify-content: space-between;
        padding: 0 18px;
        flex-shrink: 0;
        z-index: 10;
      ">
        <div style="display: flex; align-items: center; gap: 10px;">
          <span style="font-size: 12px; color: ${inkMuted};">Workflows /</span>
          <span style="font-size: 13.5px; font-weight: 700; color: ${ink};">sec-incident-auto-triage</span>

          <span style="
            padding: 2px 8px;
            background: ${infoTint};
            color: ${info};
            border: 1px solid ${infoBorder};
            border-radius: 4px;
            font-size: 10.5px;
            font-weight: 700;
            letter-spacing: 0.3px;
          ">v2.4.1 · ACTIVE</span>

          <span style="font-size: 11.5px; color: ${inkMuted};">
            Trigger: <span style="color: ${primary}; font-weight: 600;">webhook.security.alert</span>
          </span>
        </div>

        <div style="display: flex; align-items: center; gap: 8px;">
          <!-- Secondary Action: Validate Schema -->
          <button style="
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 5px 12px;
            background: ${surface};
            border: 1px solid ${border};
            border-radius: 6px;
            font-size: 11.5px;
            font-weight: 600;
            color: ${inkMuted};
            cursor: pointer;
          ">
            <span style="color: ${info};">${icons.check}</span>
            <span>Validate Schema</span>
          </button>

          <!-- Secondary Action: Test Execution -->
          <button style="
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 5px 12px;
            background: ${surface};
            border: 1px solid ${border};
            border-radius: 6px;
            font-size: 11.5px;
            font-weight: 600;
            color: ${ink};
            cursor: pointer;
          ">
            <span style="color: ${primary};">${icons.play}</span>
            <span>Test Execution</span>
          </button>

          <!-- Primary Save Button with Refined Copper Accent & Hover -->
          <button style="
            display: flex;
            align-items: center;
            gap: 7px;
            padding: 5px 14px;
            background: ${primary};
            border: 1px solid ${primaryBorder};
            border-radius: 6px;
            font-size: 12px;
            font-weight: 700;
            color: #FFFFFF;
            cursor: pointer;
            box-shadow: 0 1px 2px rgba(0,0,0,0.15);
          ">
            ${icons.save}
            <span>Save Flow</span>
          </button>
        </div>
      </div>

      <!-- 3. Workflow Editor Main Area (DAG Visualizer on Left + YAML Code on Right) -->
      <div style="
        display: flex;
        flex: 1.35;
        border-bottom: 1px solid ${border};
        overflow: hidden;
      ">
        <!-- 3A. Left: DAG Workflow Graph Canvas -->
        <div style="
          flex: 1.05;
          background-color: ${surface};
          background-image: radial-gradient(${gridDot} 1px, transparent 1px);
          background-size: 20px 20px;
          position: relative;
          border-right: 1px solid ${border};
          overflow: hidden;
        ">
          <!-- Canvas Top Tag -->
          <div style="
            position: absolute;
            top: 14px;
            left: 16px;
            display: flex;
            align-items: center;
            gap: 8px;
            background: ${surface};
            border: 1px solid ${border};
            border-radius: 8px;
            padding: 5px 12px;
            box-shadow: 0 1px 3px rgba(0,0,0,0.1);
            z-index: 10;
          ">
            <span style="font-size: 11.5px; font-weight: 700; color: ${primary};">Active DAG Runtime</span>
            <span style="height: 14px; width: 1px; background: ${border};"></span>
            <span style="font-size: 11px; color: ${info}; font-family: ui-monospace, Menlo, monospace;">6 Nodes · 5 Active Paths · Latency: 0.8ms</span>
          </div>

          <!-- Canvas Bottom Zoom & Controls -->
          <div style="
            position: absolute;
            bottom: 14px;
            right: 16px;
            display: flex;
            align-items: center;
            gap: 6px;
            background: ${surface};
            border: 1px solid ${border};
            border-radius: 8px;
            padding: 4px 10px;
            box-shadow: 0 1px 3px rgba(0,0,0,0.1);
            z-index: 10;
            font-size: 11.5px;
            color: ${inkMuted};
          ">
            <span>Canvas: <strong>100%</strong></span>
            <span style="padding: 1px 5px; background: ${surfaceAlt}; border-radius: 4px; font-weight: 600;">Fit View</span>
          </div>

          <!-- SVG Visual Connections & Highlighted Workflow Paths (Crisp Vector, No Glows) -->
          <div style="position: absolute; inset: 0;">
            <svg style="width: 100%; height: 100%; position: absolute; inset: 0; pointer-events: none;">
              <defs>
                <marker id="marker-primary-${mode}" viewBox="0 0 10 10" refX="7" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
                  <path d="M 0 1.5 L 8 5 L 0 8.5 z" fill="${primary}" />
                </marker>
                <marker id="marker-info-${mode}" viewBox="0 0 10 10" refX="7" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
                  <path d="M 0 1.5 L 8 5 L 0 8.5 z" fill="${info}" />
                </marker>
                <marker id="marker-success-${mode}" viewBox="0 0 10 10" refX="7" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
                  <path d="M 0 1.5 L 8 5 L 0 8.5 z" fill="${success}" />
                </marker>
                <marker id="marker-faint-${mode}" viewBox="0 0 10 10" refX="7" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
                  <path d="M 0 1.5 L 8 5 L 0 8.5 z" fill="${inkFaint}" />
                </marker>
              </defs>

              <!-- Path 1: Trigger -> Policy Gate (Crisp Primary Copper Stroke) -->
              <path d="M 215 130 C 265 130, 275 130, 310 130"
                fill="none"
                stroke="${primary}"
                stroke-width="2.5"
                marker-end="url(#marker-primary-${mode})"
              />

              <!-- Path 2: Policy Gate -> LLM Triage Agent (Crisp Primary Copper Flow) -->
              <path d="M 505 130 C 545 130, 555 220, 595 220"
                fill="none"
                stroke="${primary}"
                stroke-width="2.5"
                marker-end="url(#marker-primary-${mode})"
              />

              <!-- Path 3: LLM Triage Agent -> Deterministic Patch Tool (Teal Secondary) -->
              <path d="M 595 330 C 550 330, 480 370, 400 395"
                fill="none"
                stroke="${info}"
                stroke-width="2"
                stroke-dasharray="6,4"
                marker-end="url(#marker-info-${mode})"
              />

              <!-- Path 4: Patch Tool -> Test Verifier (Success Green) -->
              <path d="M 400 485 C 450 510, 530 520, 595 520"
                fill="none"
                stroke="${success}"
                stroke-width="2"
                marker-end="url(#marker-success-${mode})"
              />

              <!-- Path 5: Verifier -> Slack Notification Sink (Copper) -->
              <path d="M 785 520 C 825 520, 845 420, 845 320"
                fill="none"
                stroke="${primary}"
                stroke-width="2.5"
                marker-end="url(#marker-primary-${mode})"
              />
            </svg>
          </div>

          <!-- NODE 1: Trigger Node -->
          <div style="
            position: absolute;
            top: 70px;
            left: 30px;
            width: 185px;
            background: ${surface};
            border: 1px solid ${primaryBorder};
            border-radius: 8px;
            box-shadow: 0 1px 3px rgba(0,0,0,0.12);
            z-index: 5;
            overflow: hidden;
          ">
            <!-- Node Header -->
            <div style="
              padding: 7px 12px;
              background: ${primaryTint};
              border-bottom: 1px solid ${primaryBorder};
              display: flex;
              align-items: center;
              justify-content: space-between;
            ">
              <div style="display: flex; align-items: center; gap: 6px;">
                <span style="color: ${primary}; display: flex;">${icons.zap}</span>
                <span style="font-size: 11px; font-weight: 800; color: ${isDark ? primaryHover : primaryText}; letter-spacing: 0.3px;">EVENT TRIGGER</span>
              </div>
              <span style="width: 7px; height: 7px; border-radius: 50%; background: ${primary};"></span>
            </div>
            <!-- Node Body -->
            <div style="padding: 10px 12px;">
              <div style="font-size: 12px; font-weight: 700; color: ${ink}; margin-bottom: 4px;">Security Alert Ingress</div>
              <div style="font-size: 10.5px; font-family: ui-monospace, Menlo, monospace; color: ${inkMuted}; word-break: break-all;">
                POST /ingress/webhook
              </div>
              <div style="
                margin-top: 8px;
                padding: 3px 6px;
                background: ${surfaceAlt};
                border: 1px solid ${borderSubtle};
                border-radius: 4px;
                font-size: 10px;
                color: ${isDark ? primaryHover : primaryText};
                font-weight: 600;
                display: inline-block;
              ">
                filter: provider == 'datadog'
              </div>
            </div>
          </div>

          <!-- NODE 2: Policy Gate Node -->
          <div style="
            position: absolute;
            top: 75px;
            left: 310px;
            width: 195px;
            background: ${surface};
            border: 1px solid ${border};
            border-radius: 8px;
            box-shadow: 0 1px 3px rgba(0,0,0,0.1);
            z-index: 5;
            overflow: hidden;
          ">
            <div style="
              padding: 7px 12px;
              background: ${surfaceAlt};
              border-bottom: 1px solid ${border};
              display: flex;
              align-items: center;
              justify-content: space-between;
            ">
              <div style="display: flex; align-items: center; gap: 6px;">
                <span style="color: ${info}; display: flex;">${icons.shield}</span>
                <span style="font-size: 11px; font-weight: 700; color: ${info};">POLICY GATE</span>
              </div>
              <span style="font-size: 10px; font-family: ui-monospace, Menlo, monospace; color: ${success}; font-weight: 700;">ALLOW</span>
            </div>
            <div style="padding: 10px 12px;">
              <div style="font-size: 12px; font-weight: 700; color: ${ink}; margin-bottom: 4px;">Admission & Quota</div>
              <div style="font-size: 10.5px; color: ${inkMuted};">Rule: verify-signature-v2</div>
              <div style="
                margin-top: 8px;
                display: flex;
                align-items: center;
                gap: 5px;
                font-size: 10.5px;
                color: ${inkFaint};
              ">
                <span>Evaluated in <strong>0.34ms</strong></span>
              </div>
            </div>
          </div>

          <!-- NODE 3: Autonomous LLM Triage Agent Node -->
          <div style="
            position: absolute;
            top: 175px;
            left: 595px;
            width: 230px;
            background: ${surface};
            border: 1px solid ${primaryBorder};
            border-radius: 8px;
            box-shadow: 0 2px 6px rgba(0,0,0,0.15);
            z-index: 6;
            overflow: hidden;
          ">
            <div style="
              padding: 8px 12px;
              background: ${primaryTint};
              border-bottom: 1px solid ${primaryBorder};
              display: flex;
              align-items: center;
              justify-content: space-between;
            ">
              <div style="display: flex; align-items: center; gap: 6px;">
                <span style="color: ${primary}; display: flex;">${icons.bot}</span>
                <span style="font-size: 11px; font-weight: 800; color: ${isDark ? primaryHover : primaryText};">AI TRIAGE AGENT</span>
              </div>
              <span style="
                padding: 1px 6px;
                background: ${primary};
                color: #FFFFFF;
                border-radius: 4px;
                font-size: 9.5px;
                font-weight: 800;
              ">ACTIVE</span>
            </div>
            <div style="padding: 10px 12px;">
              <div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 4px;">
                <span style="font-size: 12px; font-weight: 700; color: ${ink};">Gemini 2.5 Flash</span>
                <span style="font-size: 10px; font-family: ui-monospace, Menlo, monospace; color: ${info}; font-weight: 600;">temp: 0.1</span>
              </div>
              <div style="font-size: 11px; color: ${inkMuted}; margin-bottom: 8px; line-height: 1.4;">
                Analyzes CVE stacktrace, synthesizes root cause hypothesis & generates remediation diff.
              </div>
              <div style="
                padding: 6px 8px;
                background: ${surfaceAlt};
                border: 1px solid ${borderSubtle};
                border-radius: 6px;
                font-size: 10.5px;
                font-family: ui-monospace, Menlo, monospace;
              ">
                <div style="color: ${isDark ? primaryHover : primaryText}; font-weight: 600;">Outputs:</div>
                <div style="color: ${inkMuted};">1. RootCauseAnalysis.md</div>
                <div style="color: ${inkMuted};">2. patch-payload.diff</div>
              </div>
            </div>
          </div>

          <!-- NODE 4: Deterministic Patch Tool -->
          <div style="
            position: absolute;
            top: 360px;
            left: 200px;
            width: 200px;
            background: ${surface};
            border: 1px solid ${border};
            border-radius: 8px;
            box-shadow: 0 1px 3px rgba(0,0,0,0.1);
            z-index: 5;
            overflow: hidden;
          ">
            <div style="
              padding: 7px 12px;
              background: ${surfaceAlt};
              border-bottom: 1px solid ${border};
              display: flex;
              align-items: center;
              justify-content: space-between;
            ">
              <div style="display: flex; align-items: center; gap: 6px;">
                <span style="color: ${info}; display: flex;">${icons.cpu}</span>
                <span style="font-size: 11px; font-weight: 700; color: ${info};">TOOL EXECUTION</span>
              </div>
              <span style="font-size: 10px; color: ${inkFaint};">Workspace</span>
            </div>
            <div style="padding: 10px 12px;">
              <div style="font-size: 12px; font-weight: 700; color: ${ink}; margin-bottom: 4px;">Git Patch Applicator</div>
              <div style="font-size: 10.5px; font-family: ui-monospace, Menlo, monospace; color: ${inkMuted};">
                tool: git.apply_diff
              </div>
              <div style="
                margin-top: 8px;
                display: flex;
                align-items: center;
                gap: 5px;
                font-size: 10.5px;
                color: ${success};
                font-weight: 600;
              ">
                ${icons.check} <span>0 Merge Conflicts</span>
              </div>
            </div>
          </div>

          <!-- NODE 5: Test & Harness Verifier -->
          <div style="
            position: absolute;
            top: 470px;
            left: 595px;
            width: 190px;
            background: ${surface};
            border: 1px solid ${border};
            border-radius: 8px;
            box-shadow: 0 1px 3px rgba(0,0,0,0.1);
            z-index: 5;
            overflow: hidden;
          ">
            <div style="
              padding: 7px 12px;
              background: ${surfaceAlt};
              border-bottom: 1px solid ${border};
              display: flex;
              align-items: center;
              justify-content: space-between;
            ">
              <div style="display: flex; align-items: center; gap: 6px;">
                <span style="color: ${success}; display: flex;">${icons.check}</span>
                <span style="font-size: 11px; font-weight: 700; color: ${success};">VERIFICATION</span>
              </div>
              <span style="font-size: 10px; color: ${success}; font-weight: 700;">PASSED</span>
            </div>
            <div style="padding: 10px 12px;">
              <div style="font-size: 12px; font-weight: 700; color: ${ink}; margin-bottom: 4px;">Test Runner Suite</div>
              <div style="font-size: 10.5px; color: ${inkMuted};">42 Unit & Scenario Tests</div>
              <div style="
                margin-top: 6px;
                font-size: 10.5px;
                font-family: ui-monospace, Menlo, monospace;
                color: ${info};
                font-weight: 600;
              ">
                exitCode: 0 (14.2s)
              </div>
            </div>
          </div>

          <!-- NODE 6: Outbox Notification Sink -->
          <div style="
            position: absolute;
            top: 250px;
            left: 845px;
            width: 175px;
            background: ${surface};
            border: 1px solid ${border};
            border-radius: 8px;
            box-shadow: 0 1px 3px rgba(0,0,0,0.1);
            z-index: 5;
            overflow: hidden;
          ">
            <div style="
              padding: 7px 12px;
              background: ${surfaceAlt};
              border-bottom: 1px solid ${border};
              display: flex;
              align-items: center;
              justify-content: space-between;
            ">
              <div style="display: flex; align-items: center; gap: 6px;">
                <span style="color: ${primary}; display: flex;">${icons.bell}</span>
                <span style="font-size: 11px; font-weight: 700; color: ${isDark ? primaryHover : primaryText};">OUTBOX SINK</span>
              </div>
            </div>
            <div style="padding: 10px 12px;">
              <div style="font-size: 12px; font-weight: 700; color: ${ink}; margin-bottom: 4px;">Slack & PagerDuty</div>
              <div style="font-size: 10.5px; color: ${inkMuted};">#sec-ops-triage</div>
              <div style="
                margin-top: 8px;
                padding: 3px 6px;
                background: ${infoTint};
                border: 1px solid ${infoBorder};
                border-radius: 4px;
                font-size: 10px;
                color: ${info};
                font-weight: 600;
              ">
                PR #4892 Created
              </div>
            </div>
          </div>
        </div>

        <!-- 3B. Right: Monaco Code Editor (YAML Definition) -->
        <div style="
          flex: 0.95;
          background: ${surface};
          display: flex;
          flex-direction: column;
          overflow: hidden;
        ">
          <!-- Editor Sub-header -->
          <div style="
            height: 38px;
            background: ${surfaceAlt};
            border-bottom: 1px solid ${border};
            display: flex;
            align-items: center;
            justify-content: space-between;
            padding: 0 14px;
            font-size: 11.5px;
            color: ${inkMuted};
          ">
            <div style="display: flex; align-items: center; gap: 8px;">
              <span style="font-weight: 600; color: ${ink};">Workflow Spec (Open Facilitator DSL v1)</span>
              <span style="padding: 1px 6px; background: ${surface}; border: 1px solid ${border}; border-radius: 4px; font-size: 10px; font-family: ui-monospace, Menlo, monospace;">schema: flow.v1.json</span>
            </div>
            <div style="display: flex; align-items: center; gap: 6px;">
              <span style="color: ${info}; font-size: 11px; font-weight: 600;">● Ready</span>
              <span style="font-family: ui-monospace, Menlo, monospace; color: ${inkFaint};">UTF-8</span>
            </div>
          </div>

          <!-- Code Lines Window -->
          <div style="
            flex: 1;
            padding: 14px 10px;
            font-family: ui-monospace, 'Cascadia Code', 'Source Code Pro', Menlo, Consolas, monospace;
            font-size: 12px;
            line-height: 1.6;
            overflow-y: auto;
            color: ${ink};
          ">
            <div style="display: flex; gap: 14px;">
              <div style="color: ${inkFaint}; text-align: right; user-select: none; width: 24px;">
                1<br>2<br>3<br>4<br>5<br>6<br>7<br>8<br>9<br>10<br>11<br>12<br>13<br>14<br>15<br>16<br>17<br>18<br>19<br>20<br>21<br>22<br>23<br>24
              </div>
              <div style="flex: 1;">
                <div><span style="color: ${primary}; font-weight: 700;">id</span>: <span style="color: ${info}; font-weight: 600;">incident-auto-triage-remediation</span></div>
                <div><span style="color: ${primary}; font-weight: 700;">version</span>: <span style="color: ${info};">"2.4.1"</span></div>
                <div><span style="color: ${primary}; font-weight: 700;">name</span>: <span style="color: ${inkMuted};">"Automated Security Incident Triage"</span></div>
                <div><span style="color: ${primary}; font-weight: 700;">schema</span>: <span style="color: ${inkFaint};">"https://open-facilitator.dev/schemas/flow.v1.json"</span></div>
                <div style="height: 6px;"></div>
                <div><span style="color: ${primary}; font-weight: 700;">trigger</span>:</div>
                <div style="padding-left: 16px;"><span style="color: ${primary};">type</span>: <span style="color: ${info}; font-weight: 600;">webhook</span></div>
                <div style="padding-left: 16px;"><span style="color: ${primary};">path</span>: <span style="color: ${inkMuted};">"/ingress/webhook/security"</span></div>
                <div style="padding-left: 16px;"><span style="color: ${primary};">filter</span>: <span style="color: ${isDark ? primaryHover : primaryText};">"headers['x-event-type'] == 'cve.alert'"</span></div>
                <div style="height: 6px;"></div>
                <div><span style="color: ${primary}; font-weight: 700;">nodes</span>:</div>
                <div style="padding-left: 16px;">- <span style="color: ${primary}; font-weight: 600;">id</span>: <span style="color: ${info};">admission_gate</span></div>
                <div style="padding-left: 32px;"><span style="color: ${primary};">type</span>: <span style="color: ${inkMuted};">policy</span></div>
                <div style="padding-left: 32px;"><span style="color: ${primary};">rule</span>: <span style="color: ${inkMuted};">"verify-signature-v2"</span></div>
                <div style="height: 4px;"></div>
                <div style="padding-left: 16px;">- <span style="color: ${primary}; font-weight: 600;">id</span>: <span style="color: ${info};">ai_triage_agent</span></div>
                <div style="padding-left: 32px;"><span style="color: ${primary};">type</span>: <span style="color: ${primary}; font-weight: 600;">agent.llm</span></div>
                <div style="padding-left: 32px;"><span style="color: ${primary};">provider</span>: <span style="color: ${info};">google-gemini-2.5-flash</span></div>
                <div style="padding-left: 32px;"><span style="color: ${primary};">temperature</span>: <span style="color: ${isDark ? primaryHover : primaryText};">0.1</span></div>
                <div style="padding-left: 32px;"><span style="color: ${primary};">system_prompt</span>: <span style="color: ${inkFaint};">"skills/security-triage.md"</span></div>
                <div style="height: 4px;"></div>
                <div style="padding-left: 16px;">- <span style="color: ${primary}; font-weight: 600;">id</span>: <span style="color: ${info};">harness_verifier</span></div>
                <div style="padding-left: 32px;"><span style="color: ${primary};">type</span>: <span style="color: ${inkMuted};">executor.harness</span></div>
                <div style="padding-left: 32px;"><span style="color: ${primary};">command</span>: <span style="color: ${success};">"make test-slice"</span></div>
                <div style="padding-left: 32px;"><span style="color: ${primary};">timeout</span>: <span style="color: ${isDark ? primaryHover : primaryText};">120s</span></div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- 4. High-Density KPI & Telemetry Strip with Solid Token Indicators -->
      <div style="
        height: 64px;
        background: ${surface};
        border-bottom: 1px solid ${border};
        display: flex;
        align-items: center;
        justify-content: space-between;
        padding: 0 20px;
        flex-shrink: 0;
      ">
        <!-- KPI 1: Live Work Throughput -->
        <div style="display: flex; align-items: center; gap: 12px;">
          <div style="
            width: 36px;
            height: 36px;
            border-radius: 8px;
            background: ${primaryTint};
            border: 1px solid ${primaryBorder};
            display: flex;
            align-items: center;
            justify-content: center;
            color: ${primary};
          ">
            ${icons.zap}
          </div>
          <div>
            <div style="font-size: 11px; font-weight: 600; color: ${inkMuted}; text-transform: uppercase; letter-spacing: 0.5px;">Work Ingress Rate</div>
            <div style="display: flex; align-items: baseline; gap: 6px;">
              <span style="font-size: 18px; font-weight: 800; color: ${ink}; font-family: ui-monospace, Menlo, monospace;">428</span>
              <span style="font-size: 11px; color: ${isDark ? primaryHover : primaryText}; font-weight: 600;">works/min</span>
            </div>
          </div>
        </div>

        <div style="height: 28px; width: 1px; background: ${border};"></div>

        <!-- KPI 2: Connected Gateways -->
        <div style="display: flex; align-items: center; gap: 12px;">
          <div style="
            width: 36px;
            height: 36px;
            border-radius: 8px;
            background: ${infoTint};
            border: 1px solid ${infoBorder};
            display: flex;
            align-items: center;
            justify-content: center;
            color: ${info};
          ">
            ${icons.link}
          </div>
          <div>
            <div style="font-size: 11px; font-weight: 600; color: ${inkMuted}; text-transform: uppercase; letter-spacing: 0.5px;">Link Hub Gateways</div>
            <div style="display: flex; align-items: baseline; gap: 6px;">
              <span style="font-size: 18px; font-weight: 800; color: ${ink}; font-family: ui-monospace, Menlo, monospace;">18 / 18</span>
              <span style="font-size: 11px; color: ${success}; font-weight: 600;">100% healthy</span>
            </div>
          </div>
        </div>

        <div style="height: 28px; width: 1px; background: ${border};"></div>

        <!-- KPI 3: Outbox P99 Latency -->
        <div style="display: flex; align-items: center; gap: 12px;">
          <div style="
            width: 36px;
            height: 36px;
            border-radius: 8px;
            background: ${surfaceAlt};
            border: 1px solid ${border};
            display: flex;
            align-items: center;
            justify-content: center;
            color: ${inkMuted};
          ">
            ${icons.monitor}
          </div>
          <div>
            <div style="font-size: 11px; font-weight: 600; color: ${inkMuted}; text-transform: uppercase; letter-spacing: 0.5px;">Outbox P99 Latency</div>
            <div style="display: flex; align-items: baseline; gap: 6px;">
              <span style="font-size: 18px; font-weight: 800; color: ${ink}; font-family: ui-monospace, Menlo, monospace;">1.2</span>
              <span style="font-size: 11px; color: ${inkMuted};">ms</span>
            </div>
          </div>
        </div>

        <div style="height: 28px; width: 1px; background: ${border};"></div>

        <!-- KPI 4: Policy Pass Rate -->
        <div style="display: flex; align-items: center; gap: 12px;">
          <div style="
            width: 36px;
            height: 36px;
            border-radius: 8px;
            background: ${successTint};
            border: 1px solid ${successBorder};
            display: flex;
            align-items: center;
            justify-content: center;
            color: ${success};
          ">
            ${icons.shield}
          </div>
          <div>
            <div style="font-size: 11px; font-weight: 600; color: ${inkMuted}; text-transform: uppercase; letter-spacing: 0.5px;">Policy Admission Gate</div>
            <div style="display: flex; align-items: baseline; gap: 6px;">
              <span style="font-size: 18px; font-weight: 800; color: ${ink}; font-family: ui-monospace, Menlo, monospace;">99.8%</span>
              <span style="font-size: 11px; color: ${success}; font-weight: 600;">compliant</span>
            </div>
          </div>
        </div>

        <div style="height: 28px; width: 1px; background: ${border};"></div>

        <!-- KPI 5: Active Leases -->
        <div style="display: flex; align-items: center; gap: 12px;">
          <div style="
            width: 36px;
            height: 36px;
            border-radius: 8px;
            background: ${primaryTint};
            border: 1px solid ${primaryBorder};
            display: flex;
            align-items: center;
            justify-content: center;
            color: ${primary};
          ">
            ${icons.cpu}
          </div>
          <div>
            <div style="font-size: 11px; font-weight: 600; color: ${inkMuted}; text-transform: uppercase; letter-spacing: 0.5px;">Active Leases</div>
            <div style="display: flex; align-items: baseline; gap: 6px;">
              <span style="font-size: 18px; font-weight: 800; color: ${isDark ? primaryHover : primaryText}; font-family: ui-monospace, Menlo, monospace;">34</span>
              <span style="font-size: 11px; color: ${inkMuted};">claimed</span>
            </div>
          </div>
        </div>
      </div>

      <!-- 5. Works Dashboard & Live Queue Table -->
      <div style="
        flex: 1;
        background: ${surfaceAlt};
        padding: 16px 20px;
        display: flex;
        flex-direction: column;
        overflow: hidden;
      ">
        <!-- Table Control Toolbar -->
        <div style="
          display: flex;
          align-items: center;
          justify-content: space-between;
          margin-bottom: 12px;
          flex-shrink: 0;
        ">
          <div style="display: flex; align-items: center; gap: 10px;">
            <span style="font-size: 14px; font-weight: 800; color: ${ink};">Live Work Requests</span>

            <!-- Filter Pills with Theme Primary Selected State -->
            <div style="display: flex; align-items: center; gap: 4px; margin-left: 8px;">
              <span style="
                padding: 3px 10px;
                background: ${primary};
                color: #FFFFFF;
                border-radius: 6px;
                font-size: 11.5px;
                font-weight: 700;
                cursor: pointer;
              ">All Works (14)</span>

              <span style="
                padding: 3px 10px;
                background: ${surface};
                border: 1px solid ${border};
                color: ${inkMuted};
                border-radius: 6px;
                font-size: 11.5px;
                font-weight: 500;
              ">Running (4)</span>

              <span style="
                padding: 3px 10px;
                background: ${surface};
                border: 1px solid ${border};
                color: ${inkMuted};
                border-radius: 6px;
                font-size: 11.5px;
                font-weight: 500;
              ">Awaiting Lease (2)</span>

              <span style="
                padding: 3px 10px;
                background: ${surface};
                border: 1px solid ${border};
                color: ${inkMuted};
                border-radius: 6px;
                font-size: 11.5px;
                font-weight: 500;
              ">Failed (0)</span>
            </div>
          </div>

          <div style="display: flex; align-items: center; gap: 8px;">
            <div style="
              display: flex;
              align-items: center;
              gap: 6px;
              padding: 4px 10px;
              background: ${surface};
              border: 1px solid ${border};
              border-radius: 6px;
              font-size: 11.5px;
              color: ${inkMuted};
            ">
              ${icons.filter}
              <span>Filter: <strong>Provider = GitHub</strong></span>
            </div>
          </div>
        </div>

        <!-- Work Items Table Container -->
        <div style="
          flex: 1;
          background: ${surface};
          border: 1px solid ${border};
          border-radius: 8px;
          overflow: hidden;
          box-shadow: 0 1px 3px rgba(0,0,0,0.08);
          display: flex;
          flex-direction: column;
        ">
          <!-- Table Header -->
          <div style="
            display: grid;
            grid-template-columns: 140px 2.2fr 1.8fr 1.3fr 130px 100px 90px;
            padding: 9px 14px;
            background: ${surfaceAlt};
            border-bottom: 1px solid ${border};
            font-size: 11px;
            font-weight: 700;
            color: ${inkMuted};
            text-transform: uppercase;
            letter-spacing: 0.4px;
          ">
            <div>Work ID</div>
            <div>Work Descriptor / Title</div>
            <div>Workflow Pipeline</div>
            <div>Gateway / Link</div>
            <div>State</div>
            <div>Duration</div>
            <div style="text-align: right;">Action</div>
          </div>

          <!-- Table Row 1 (Running with Theme Primary Accent Pill) -->
          <div style="
            display: grid;
            grid-template-columns: 140px 2.2fr 1.8fr 1.3fr 130px 100px 90px;
            padding: 10px 14px;
            border-bottom: 1px solid ${borderSubtle};
            align-items: center;
            font-size: 12px;
            background: ${primaryTint};
          ">
            <div style="font-family: ui-monospace, Menlo, monospace; font-weight: 700; color: ${primary};">
              wrk_01HZX89
            </div>
            <div style="display: flex; align-items: center; gap: 8px;">
              <span style="font-weight: 600; color: ${ink};">Security Remediation: CVE-2026-4418</span>
              <span style="padding: 1px 5px; background: ${surface}; border: 1px solid ${primaryBorder}; color: ${isDark ? primaryHover : primaryText}; border-radius: 4px; font-size: 10px; font-weight: 700;">HIGH</span>
            </div>
            <div style="color: ${inkMuted}; font-family: ui-monospace, Menlo, monospace; font-size: 11px;">
              incident-auto-triage-remediation
            </div>
            <div style="display: flex; align-items: center; gap: 6px; color: ${info}; font-family: ui-monospace, Menlo, monospace; font-size: 11px;">
              <span style="width: 6px; height: 6px; border-radius: 50%; background: ${info};"></span>
              <span>gw-us-east-1a</span>
            </div>
            <div>
              <span style="
                padding: 3px 8px;
                background: ${primary};
                color: #FFFFFF;
                border-radius: 12px;
                font-size: 10.5px;
                font-weight: 800;
                display: inline-flex;
                align-items: center;
                gap: 4px;
              ">
                <span style="width: 5px; height: 5px; border-radius: 50%; background: #FFFFFF;"></span>
                RUNNING
              </span>
            </div>
            <div style="font-family: ui-monospace, Menlo, monospace; color: ${inkMuted}; font-size: 11.5px;">
              14.2s
            </div>
            <div style="text-align: right;">
              <span style="color: ${isDark ? primaryHover : primaryText}; font-weight: 600; cursor: pointer; font-size: 11.5px;">View Live ▶</span>
            </div>
          </div>

          <!-- Table Row 2 (Admitted State) -->
          <div style="
            display: grid;
            grid-template-columns: 140px 2.2fr 1.8fr 1.3fr 130px 100px 90px;
            padding: 10px 14px;
            border-bottom: 1px solid ${borderSubtle};
            align-items: center;
            font-size: 12px;
            background: ${surface};
          ">
            <div style="font-family: ui-monospace, Menlo, monospace; font-weight: 700; color: ${ink};">
              wrk_01HZX87
            </div>
            <div style="display: flex; align-items: center; gap: 8px;">
              <span style="font-weight: 500; color: ${ink};">Release Chore: Bump Dependencies Net10</span>
              <span style="padding: 1px 5px; background: ${surfaceAlt}; color: ${inkMuted}; border-radius: 4px; font-size: 10px;">CHORE</span>
            </div>
            <div style="color: ${inkMuted}; font-family: ui-monospace, Menlo, monospace; font-size: 11px;">
              deps-auto-upgrade-pipeline
            </div>
            <div style="display: flex; align-items: center; gap: 6px; color: ${info}; font-family: ui-monospace, Menlo, monospace; font-size: 11px;">
              <span style="width: 6px; height: 6px; border-radius: 50%; background: ${info};"></span>
              <span>gw-eu-west-2b</span>
            </div>
            <div>
              <span style="
                padding: 3px 8px;
                background: ${infoTint};
                color: ${info};
                border: 1px solid ${infoBorder};
                border-radius: 12px;
                font-size: 10.5px;
                font-weight: 700;
              ">
                ADMITTED
              </span>
            </div>
            <div style="font-family: ui-monospace, Menlo, monospace; color: ${inkMuted}; font-size: 11.5px;">
              2.1s
            </div>
            <div style="text-align: right;">
              <span style="color: ${inkMuted}; font-weight: 500; font-size: 11.5px;">Inspect</span>
            </div>
          </div>

          <!-- Table Row 3 (Succeeded State) -->
          <div style="
            display: grid;
            grid-template-columns: 140px 2.2fr 1.8fr 1.3fr 130px 100px 90px;
            padding: 10px 14px;
            border-bottom: 1px solid ${borderSubtle};
            align-items: center;
            font-size: 12px;
            background: ${surface};
          ">
            <div style="font-family: ui-monospace, Menlo, monospace; font-weight: 700; color: ${ink};">
              wrk_01HZX80
            </div>
            <div style="display: flex; align-items: center; gap: 8px;">
              <span style="font-weight: 500; color: ${ink};">Contract Codegen: Schema v1.8 Sync</span>
              <span style="padding: 1px 5px; background: ${surfaceAlt}; color: ${inkMuted}; border-radius: 4px; font-size: 10px;">AUTO</span>
            </div>
            <div style="color: ${inkMuted}; font-family: ui-monospace, Menlo, monospace; font-size: 11px;">
              protocol-codegen-verify
            </div>
            <div style="display: flex; align-items: center; gap: 6px; color: ${info}; font-family: ui-monospace, Menlo, monospace; font-size: 11px;">
              <span style="width: 6px; height: 6px; border-radius: 50%; background: ${info};"></span>
              <span>gw-us-east-1a</span>
            </div>
            <div>
              <span style="
                padding: 3px 8px;
                background: ${successTint};
                color: ${success};
                border: 1px solid ${successBorder};
                border-radius: 12px;
                font-size: 10.5px;
                font-weight: 700;
              ">
                SUCCEEDED
              </span>
            </div>
            <div style="font-family: ui-monospace, Menlo, monospace; color: ${inkMuted}; font-size: 11.5px;">
              48.6s
            </div>
            <div style="text-align: right;">
              <span style="color: ${inkMuted}; font-weight: 500; font-size: 11.5px;">Logs</span>
            </div>
          </div>

          <!-- Table Row 4 (Streaming Deliveries State) -->
          <div style="
            display: grid;
            grid-template-columns: 140px 2.2fr 1.8fr 1.3fr 130px 100px 90px;
            padding: 10px 14px;
            align-items: center;
            font-size: 12px;
            background: ${surface};
          ">
            <div style="font-family: ui-monospace, Menlo, monospace; font-weight: 700; color: ${ink};">
              wrk_01HZX75
            </div>
            <div style="display: flex; align-items: center; gap: 8px;">
              <span style="font-weight: 500; color: ${ink};">Scenario Gate: Run Manifest Walk #884</span>
            </div>
            <div style="color: ${inkMuted}; font-family: ui-monospace, Menlo, monospace; font-size: 11px;">
              scenario-gate-runner
            </div>
            <div style="display: flex; align-items: center; gap: 6px; color: ${info}; font-family: ui-monospace, Menlo, monospace; font-size: 11px;">
              <span style="width: 6px; height: 6px; border-radius: 50%; background: ${info};"></span>
              <span>gw-ap-south-1</span>
            </div>
            <div>
              <span style="
                padding: 3px 8px;
                background: ${primaryTint};
                color: ${isDark ? primaryHover : primaryText};
                border: 1px solid ${primaryBorder};
                border-radius: 12px;
                font-size: 10.5px;
                font-weight: 700;
              ">
                STREAMING
              </span>
            </div>
            <div style="font-family: ui-monospace, Menlo, monospace; color: ${inkMuted}; font-size: 11.5px;">
              1m 04s
            </div>
            <div style="text-align: right;">
              <span style="color: ${isDark ? primaryHover : primaryText}; font-weight: 600; font-size: 11.5px;">Tail Log</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  `;
}

// Generate Side-by-Side Presentation Mockup HTML
function generateSideBySideShowcaseHTML(themeConfig) {
  const lightUI = generateFullUI(themeConfig, "light");
  const darkUI = generateFullUI(themeConfig, "dark");

  const primary = themeConfig.dark.primary;
  const info = themeConfig.dark.info;
  const success = themeConfig.dark.success;
  const warning = themeConfig.dark.warning;
  const error = themeConfig.dark.error;

  const bgDark = themeConfig.dark.bg;
  const bgLight = themeConfig.light.bg;

  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <title>${themeConfig.title}</title>
  <style>
    * {
      margin: 0;
      padding: 0;
      box-sizing: border-box;
    }
    body {
      background: #15181E;
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
      width: 2880px;
      height: 1800px;
      overflow: hidden;
      display: flex;
      flex-direction: column;
    }

    .showcase-banner {
      height: 80px;
      background: #1C2028;
      border-bottom: 2px solid #2B313E;
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 0 32px;
      flex-shrink: 0;
    }

    .banner-title {
      display: flex;
      align-items: center;
      gap: 14px;
    }

    .banner-logo {
      width: 40px;
      height: 40px;
      background: ${primary};
      border-radius: 9px;
      display: flex;
      align-items: center;
      justify-content: center;
      box-shadow: 0 2px 6px rgba(0,0,0,0.3);
    }

    .banner-heading {
      font-size: 20px;
      font-weight: 800;
      color: #FFFFFF;
      letter-spacing: -0.4px;
    }

    .banner-subheading {
      font-size: 13px;
      color: #9CA3AF;
      margin-top: 2px;
    }

    .banner-specs {
      display: flex;
      align-items: center;
      gap: 10px;
    }

    .spec-pill {
      display: flex;
      align-items: center;
      gap: 6px;
      padding: 6px 11px;
      background: #252B35;
      border: 1px solid #363E4D;
      border-radius: 6px;
      font-size: 11.5px;
      font-family: ui-monospace, Menlo, monospace;
      color: #D1D5DB;
    }

    .spec-color-dot {
      width: 9px;
      height: 9px;
      border-radius: 50%;
    }

    .showcase-stage {
      flex: 1;
      display: flex;
      position: relative;
      overflow: hidden;
    }

    .stage-half {
      flex: 1;
      height: 100%;
      position: relative;
      overflow: hidden;
      display: flex;
      flex-direction: column;
    }

    .stage-divider {
      position: absolute;
      left: 50%;
      top: 0;
      bottom: 0;
      width: 2px;
      background: #3F4754;
      z-index: 50;
      transform: translateX(-50%);
    }

    .theme-label-badge {
      position: absolute;
      top: 14px;
      padding: 5px 14px;
      background: #1C2028;
      border: 1px solid #363E4D;
      border-radius: 20px;
      font-size: 11px;
      font-weight: 800;
      letter-spacing: 0.6px;
      color: #F3F4F6;
      display: flex;
      align-items: center;
      gap: 8px;
      box-shadow: 0 2px 6px rgba(0,0,0,0.25);
      z-index: 40;
    }

    .theme-label-left {
      left: 20px;
    }

    .theme-label-right {
      right: 20px;
    }
  </style>
</head>
<body>
  <!-- Top Showcase Banner -->
  <div class="showcase-banner">
    <div class="banner-title">
      <div class="banner-logo">
        <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="#FFFFFF" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round">
          <polygon points="12 2 2 7 12 12 22 7 12 2"></polygon>
          <polyline points="2 17 12 22 22 17"></polyline>
          <polyline points="2 12 12 17 22 12"></polyline>
        </svg>
      </div>
      <div>
        <div class="banner-heading">${themeConfig.title}</div>
        <div class="banner-subheading">${themeConfig.subtitle}</div>
      </div>
    </div>

    <!-- Color Tokens Pill Strip -->
    <div class="banner-specs">
      <div class="spec-pill">
        <span class="spec-color-dot" style="background: ${primary};"></span>
        <span>Primary: ${primary}</span>
      </div>

      <div class="spec-pill">
        <span class="spec-color-dot" style="background: ${info};"></span>
        <span>Info: ${info}</span>
      </div>

      <div class="spec-pill">
        <span class="spec-color-dot" style="background: ${success};"></span>
        <span>Success: ${success}</span>
      </div>

      <div class="spec-pill">
        <span class="spec-color-dot" style="background: ${warning};"></span>
        <span>Warning: ${warning}</span>
      </div>

      <div class="spec-pill">
        <span class="spec-color-dot" style="background: ${error};"></span>
        <span>Error: ${error}</span>
      </div>

      <div class="spec-pill" style="border-color: ${primary}; color: #FFFFFF; background: ${primary}22;">
        <span>${themeConfig.badgeText}</span>
      </div>
    </div>
  </div>

  <!-- Main Showcase Stage: Split Left (Light) & Right (Dark) -->
  <div class="showcase-stage">
    <!-- Left Half: Light Theme -->
    <div class="stage-half">
      <div class="theme-label-badge theme-label-left">
        <span style="width: 8px; height: 8px; border-radius: 50%; background: ${primary};"></span>
        <span>OPEN FACILITATOR — LIGHT THEME (${bgLight})</span>
      </div>
      ${lightUI}
    </div>

    <!-- Center Precision Divider -->
    <div class="stage-divider"></div>

    <!-- Right Half: Dark Theme -->
    <div class="stage-half">
      <div class="theme-label-badge theme-label-right">
        <span style="width: 8px; height: 8px; border-radius: 50%; background: ${primary};"></span>
        <span>OPEN FACILITATOR — DARK THEME (${bgDark})</span>
      </div>
      ${darkUI}
    </div>
  </div>
</body>
</html>`;
}

// Generate Standalone Full Theme HTML (Light or Dark)
function generateStandaloneThemeHTML(themeConfig, mode = "dark") {
  const ui = generateFullUI(themeConfig, mode);
  const isDark = mode === "dark";
  const tokens = isDark ? themeConfig.dark : themeConfig.light;

  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <title>${themeConfig.name} - ${isDark ? "Dark" : "Light"} Theme (${tokens.primary})</title>
  <style>
    * { margin: 0; padding: 0; box-sizing: border-box; }
    body {
      background: ${tokens.bg};
      width: 2560px;
      height: 1440px;
      overflow: hidden;
    }
  </style>
</head>
<body>
  ${ui}
</body>
</html>`;
}

// Markdown Specification Generator
function generateThemeSpecMarkdown(themeConfig) {
  const tDark = themeConfig.dark;
  const tLight = themeConfig.light;

  return `# ${themeConfig.title}

> Canonical Theme Directory: \`.omp/evidence/theme/copper/\`

## Overview
${themeConfig.subtitle}.

This theme provides a high-contrast, developer-tool aesthetic influenced by studio environments (Linear, Vercel, JetBrains IDEs) and optimized for Open Facilitator's autonomous kernel, DAG workflow editor, YAML Monaco pane, and live Link Hub monitoring.

### Refinements Applied
- **Unslop Quality Philosophy:** Removed all hazy gaussian blur glowing filters and blurry drop shadows; replaced with crisp 1px borders, solid fills, and subtle 1px/2px elevation shadows.
- **Opaque Color Architecture:** Eliminated excessive transparent \`rgba\` overlays in favor of solid, high-contrast semantic tint tokens (\`primaryTint\`, \`infoTint\`, \`successTint\`, \`warningTint\`, \`errorTint\`).
- **Secondary Accent Darkening:** Deepened secondary tones (Teal \`${tDark.info}\` / \`${tLight.info}\`, Olive Emerald \`${tDark.success}\` / \`${tLight.success}\`, Amber Gold \`${tDark.warning}\` / \`${tLight.warning}\`, Brick Red \`${tDark.error}\` / \`${tLight.error}\`) for grounded, professional contrast.
- **Modern Clean Light Theme Ground:** Transitioned light theme background from rustic beige to a crisp, technical cool-neutral grey (\`${tLight.bg}\`) with pure white card surfaces (\`${tLight.surface}\`).

## Token Reference

### Dark Mode Tokens (\`${tDark.bg}\`)
| Token | Hex Value | Role |
|---|---|---|
| Ground Background | \`${tDark.bg}\` | Primary canvas & app background |
| Surface Primary | \`${tDark.surface}\` | Header, cards, editor, panels |
| Surface Alt | \`${tDark.surfaceAlt}\` | Sub-toolbars, input containers, table headers |
| Surface Highlight | \`${tDark.surfaceHighlight}\` | Active selection, elevated cards |
| Border Standard | \`${tDark.border}\` | Primary structural dividers |
| Border Subtle | \`${tDark.borderSubtle}\` | Inner list separators, subtle grids |
| Primary Accent | \`${tDark.primary}\` | Active tabs, trigger nodes, save buttons, selected pills |
| Primary Hover | \`${tDark.primaryHover}\` | Interactive hover states, active paths |
| Primary Tint (Solid) | \`${tDark.primaryTint}\` | Background fill for selected rows, active tabs, node headers |
| Primary Border (Solid) | \`${tDark.primaryBorder}\` | Border for active elements |
| Info / Link Secondary | \`${tDark.info}\` | Link Hub status, policy gates, schema badges |
| Info Tint (Solid) | \`${tDark.infoTint}\` | Background for admission gates, link hub indicators |
| Success State | \`${tDark.success}\` | Verified runs, passing tests, healthy gateways |
| Success Tint (Solid) | \`${tDark.successTint}\` | Background for succeeded rows and passed verification |
| Warning State | \`${tDark.warning}\` | High load, queue backlog, lease expiry warnings |
| Error State | \`${tDark.error}\` | Ingress rejections, merge conflicts, failed steps |
| Text Ink | \`${tDark.ink}\` | High-contrast headings and active labels |
| Text Muted | \`${tDark.inkMuted}\` | Secondary labels, descriptions, schema keys |
| Text Faint | \`${tDark.inkFaint}\` | Timestamps, metadata, line numbers |

### Light Mode Tokens (\`${tLight.bg}\`)
| Token | Hex Value | Role |
|---|---|---|
| Ground Background | \`${tLight.bg}\` | Modern cool-neutral technical studio ground |
| Surface Primary | \`${tLight.surface}\` | Pure white cards and panels |
| Surface Alt | \`${tLight.surfaceAlt}\` | Crisp cool-tinted toolbars and inactive pills |
| Border Standard | \`${tLight.border}\` | Crisp structural border |
| Border Subtle | \`${tLight.borderSubtle}\` | Subtle row separators |
| Primary Accent | \`${tLight.primary}\` | Primary brand accent and active states |
| Primary Hover | \`${tLight.primaryHover}\` | Interactive button and link hover |
| Primary Text | \`${tLight.primaryText}\` | Darkened copper text for high contrast on light ground |
| Primary Tint (Solid) | \`${tLight.primaryTint}\` | Solid light copper tint for active rows and headers |
| Primary Border (Solid) | \`${tLight.primaryBorder}\` | Border for active cards and pills |
| Info / Secondary | \`${tLight.info}\` | Secondary status and Link indicators |
| Info Tint (Solid) | \`${tLight.infoTint}\` | Solid light teal tint |
| Success State | \`${tLight.success}\` | Verified steps and healthy indicators |
| Success Tint (Solid) | \`${tLight.successTint}\` | Solid light green tint |
| Warning State | \`${tLight.warning}\` | Warnings and attention pills |
| Error State | \`${tLight.error}\` | Critical errors and rejections |
| Text Ink | \`${tLight.ink}\` | High-contrast dark typography |
| Text Muted | \`${tLight.inkMuted}\` | Secondary body text |
| Text Faint | \`${tLight.inkFaint}\` | Timestamps, metadata, line numbers |

## Generated Artifacts
- \`open-facilitator-copper-theme-showcase.png\` (2880 × 1800 side-by-side presentation mockup)
- \`showcase-side-by-side.png\` & \`theme-showcase.png\` (convenience aliases)
- \`open-facilitator-light-theme.png\` (2560 × 1440 standalone light theme)
- \`open-facilitator-dark-theme.png\` (2560 × 1440 standalone dark theme)
- \`showcase-template.html\`, \`light-theme-template.html\`, \`dark-theme-template.html\` (interactive HTML sources)
`;
}

// Main execution function
async function main() {
  const edgePath = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";

  if (!fs.existsSync(edgePath)) {
    console.error("Edge executable not found at:", edgePath);
    process.exit(1);
  }

  const targetDir = COPPER_DIR;
  if (!fs.existsSync(targetDir)) {
    fs.mkdirSync(targetDir, { recursive: true });
  }

  console.log(`\n========================================`);
  console.log(`Generating Refined Theme: ${THEME.name} (${THEME.id})`);
  console.log(`Target Directory: ${targetDir}`);
  console.log(`========================================`);

  const showcaseHTMLPath = path.join(targetDir, "showcase-template.html");
  const lightHTMLPath = path.join(targetDir, "light-theme-template.html");
  const darkHTMLPath = path.join(targetDir, "dark-theme-template.html");
  const specMDPath = path.join(targetDir, "THEME_SPEC.md");

  fs.writeFileSync(showcaseHTMLPath, generateSideBySideShowcaseHTML(THEME), "utf8");
  fs.writeFileSync(lightHTMLPath, generateStandaloneThemeHTML(THEME, "light"), "utf8");
  fs.writeFileSync(darkHTMLPath, generateStandaloneThemeHTML(THEME, "dark"), "utf8");
  fs.writeFileSync(specMDPath, generateThemeSpecMarkdown(THEME), "utf8");

  console.log("HTML templates and THEME_SPEC.md written.");

  // 1. Render Side-by-side Showcase Image
  const showcasePNG = path.join(targetDir, `open-facilitator-${THEME.id}-theme-showcase.png`);
  console.log(`Rendering side-by-side showcase image: ${showcasePNG}`);
  execFileSync(edgePath, [
    "--headless",
    "--disable-gpu",
    `--screenshot=${showcasePNG}`,
    "--window-size=2880,1800",
    "--hide-scrollbars",
    `file:///${showcaseHTMLPath.replace(/\\/g, "/")}`,
  ]);

  // Copy aliases
  fs.copyFileSync(showcasePNG, path.join(targetDir, "theme-showcase.png"));
  fs.copyFileSync(showcasePNG, path.join(targetDir, "showcase-side-by-side.png"));

  // 2. Render Standalone Light Theme
  const lightPNG = path.join(targetDir, "open-facilitator-light-theme.png");
  console.log(`Rendering light theme image: ${lightPNG}`);
  execFileSync(edgePath, [
    "--headless",
    "--disable-gpu",
    `--screenshot=${lightPNG}`,
    "--window-size=2560,1440",
    "--hide-scrollbars",
    `file:///${lightHTMLPath.replace(/\\/g, "/")}`,
  ]);

  // 3. Render Standalone Dark Theme
  const darkPNG = path.join(targetDir, "open-facilitator-dark-theme.png");
  console.log(`Rendering dark theme image: ${darkPNG}`);
  execFileSync(edgePath, [
    "--headless",
    "--disable-gpu",
    `--screenshot=${darkPNG}`,
    "--window-size=2560,1440",
    "--hide-scrollbars",
    `file:///${darkHTMLPath.replace(/\\/g, "/")}`,
  ]);

  console.log(`\nRefined Copper Theme generated successfully!`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
