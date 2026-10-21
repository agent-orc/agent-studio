/**
 * AGT-2010 — Navigation active-item evidence harness.
 *
 * Renders BEFORE / AFTER, dark / light before-after composites for the unified
 * active side-menu-item treatment, so a reviewer can independently verify the
 * "colour-filled active entry, one concept everywhere" change.
 *
 * WHY a harness instead of a live-backend `--real` e2e capture:
 *   The change is CSS/token-only. To be trustworthy the render must use the
 *   *real* compiled CSS, so this harness feeds Playwright:
 *     1. the REAL global design tokens  — `src/styles.scss` compiled with the
 *        SAME dart-sass the app build uses (both themes: `:root` + the
 *        `html[data-studio-theme='light']` bridge), and
 *     2. the REAL component SCSS — `tree-row`, `list-row`, `section-header`,
 *        `count-badge`, the CLI-sessions list and the Workspace-settings rail,
 *        each compiled straight from the working tree (the AFTER state).
 *   The BEFORE panel re-applies the verbatim pre-AGT-2010 active rules (copied
 *   into `BEFORE_OVERRIDES` below, straight from the commit diff) on top of the
 *   same tokens, so the contrast is faithful and fully self-contained — no
 *   backend, no git checkout, no network. Because the tokens and component CSS
 *   are the ones the app actually ships, the render is representative of the
 *   running app for this CSS-only change.
 *
 * Evidence labelling (docs/system/contracts/protocol-style.md §4.1 / §4.4): the output
 * PNGs carry `--mocked` (no live backend; synthetic DOM + real compiled CSS).
 *
 * Run:
 *   node e2e/visual-evidence/nav-active-state.harness.mjs
 *   NAV_EVIDENCE_OUT=/abs/out/dir node e2e/visual-evidence/nav-active-state.harness.mjs
 */
import { chromium } from 'playwright';
import * as sass from 'sass';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const FRONTEND = path.resolve(__dirname, '..', '..');
const SRC = path.join(FRONTEND, 'src');
const OUT = process.env.NAV_EVIDENCE_OUT
  ? path.resolve(process.env.NAV_EVIDENCE_OUT)
  : path.join(__dirname, '__screenshots__', 'nav-active-state');

const rel = (...p) => path.join(SRC, ...p);

/** Compile a whole SCSS entry (resolves its own `@use` graph). */
function compileFile(absPath) {
  return sass.compile(absPath, { style: 'expanded', loadPaths: [SRC] }).css;
}
/** Compile a self-contained component SCSS partial (no external `@use`). */
function compileComponent(absPath) {
  const source = fs.readFileSync(absPath, 'utf8');
  return sass.compileString(source, { style: 'expanded', loadPaths: [SRC, FRONTEND] }).css;
}

// The REAL app tokens + light-theme bridge (single source of truth).
const TOKENS_CSS = compileFile(path.join(SRC, 'styles.scss'));

// The REAL, current (AFTER) component SCSS the app ships.
const COMPONENT_CSS = [
  compileComponent(rel('app', 'components', 'tree-row', 'tree-row.component.scss')),
  compileComponent(rel('app', 'components', 'list-row', 'list-row.component.scss')),
  compileComponent(rel('app', 'components', 'section-header', 'section-header.component.scss')),
  compileComponent(rel('app', 'components', 'count-badge', 'count-badge.component.scss')),
  compileComponent(rel('app', 'features', 'cli', 'components', 'cli-sessions-panel', 'cli-sessions-panel.scss')),
  compileComponent(rel('app', 'features', 'shell', 'components', 'workspace-overlays', 'workspace-overlays.component.scss')),
].join('\n');

// Verbatim pre-AGT-2010 active rules (copied from the commit 0da9af28 diff).
// Appended after the AFTER component CSS to recreate the BEFORE look on the
// same real tokens. Each surface's former active state was a subtle grey/tint
// or an off-brand hard-coded colour with no side bar.
const BEFORE_OVERRIDES = `
/* tree-row (Explorer tree, Deck rail, Prompt catalogue) — former 14% grey-ish tint, no bar */
.tree-row--active { background: color-mix(in srgb, var(--studio-accent) 14%, var(--studio-bg-selected)); color: var(--studio-fg-strong); font-weight: 600; box-shadow: none; }
.tree-row--active:hover { background: color-mix(in srgb, var(--studio-accent) 18%, var(--studio-bg-selected)); }
/* list-row (Open-tabs list) — former plain grey bg-selected, no accent, no bar */
.list-row--active { background: var(--studio-bg-selected); color: var(--studio-fg-strong); box-shadow: none; }
/* CLI sessions — former hard-coded off-brand blue that ignored the theme */
.session--active { background: rgba(96,165,250,.14); color: #f8fafc; box-shadow: none; }
/* Workspace / Orchestrator settings rail — former plain grey bg-selected, no bar */
.ws-settings__rail-item--active { background: var(--studio-bg-selected); color: var(--studio-fg-strong); font-weight: 600; box-shadow: none; }
/* Focused-view task rail — former hard-coded off-brand indigo that ignored the theme */
.task-nav__item--active { background: rgba(99,102,241,0.16); border-color: rgba(99,102,241,0.45); box-shadow: 0 0 0 1px rgba(99,102,241,0.15); }
`;

// Minimal extra rules for the two bespoke surfaces the app defines outside a
// component SCSS file we compile above (the focused-view task rail lives in the
// large app.scss). Verbatim AFTER rules from the diff, so both states are real.
const TASK_NAV_AFTER = `
.task-nav__item { display:flex; flex-direction:column; gap:2px; padding:8px 10px; border:1px solid var(--studio-border); border-radius:8px; background:transparent; color:var(--studio-fg); text-align:left; cursor:pointer; }
.task-nav__item--active { background: var(--studio-nav-active-bg); border-color: color-mix(in srgb, var(--studio-accent) 45%, transparent); box-shadow: inset 3px 0 0 0 var(--studio-nav-active-bar); }
.task-nav__item-title { font-size:14px; font-weight:600; }
.task-nav__item-sub { font-size:11px; color: var(--studio-fg-muted); }
`;

// ---------------------------------------------------------------------------
// Faithful DOM builders (class names mirror the real component templates).
// ---------------------------------------------------------------------------
const GLYPH = `<span class="tree-row__glyph-icon"><span style="display:inline-block;width:12px;height:12px;border:1.5px solid currentColor;border-radius:3px;opacity:.65"></span></span>`;
const CHEV = (dir) => `<span class="tree-row__chev">${dir === 'down' ? '▾' : '▸'}</span>`;
const CHEV_PH = `<span class="tree-row__chev tree-row__chev--placeholder"></span>`;

function badge(value, active) {
  return `<span class="count-badge${active ? ' count-badge--active' : ''}">${value}</span>`;
}
function sectionHeader(title, count) {
  return `<div class="section-header section-header--static"><span class="section-header__title">${title}</span>${count != null ? badge(count) : ''}</div>`;
}
function treeRow({ label, level = 'root', chev = null, glyph = true, count = null, active = false }) {
  const chevHtml = chev === null ? (level === 'child' ? '' : CHEV_PH) : CHEV(chev);
  return `<button class="tree-row tree-row--${level}${active ? ' tree-row--active' : ''}"${active ? ' aria-current="page"' : ''}>`
    + `${chevHtml}${glyph ? GLYPH : ''}<span class="tree-row__name">${label}</span>${count != null ? badge(count, active) : ''}</button>`;
}

// Level 1 — Explorer workspace tree.
const RAIL_L1 = [
  sectionHeader('WORKSPACES', 3),
  treeRow({ label: 'Default workspace', chev: 'down', count: 3 }),
  treeRow({ label: 'Agent Studio', chev: 'down', count: 27 }),
  treeRow({ label: 'Board', level: 'child', glyph: true }),
  treeRow({ label: 'Deck', level: 'child', glyph: true, active: true }),
  treeRow({ label: 'Wiki', level: 'child', glyph: true }),
  treeRow({ label: 'Backlog', level: 'child', glyph: true }),
  treeRow({ label: 'Dossiers', level: 'child', glyph: true }),
].join('');

// Level 2 — Deck rail (INSIGHT / QUALITY sections).
const RAIL_L2 = [
  sectionHeader('INSIGHT'),
  treeRow({ label: 'Overview', glyph: true, active: true }),
  treeRow({ label: 'Project URLs', glyph: true }),
  treeRow({ label: 'Git View', glyph: true }),
  treeRow({ label: 'Visual Evidence', glyph: true }),
  treeRow({ label: 'Drift', glyph: true }),
  treeRow({ label: 'Observability', glyph: true }),
  sectionHeader('QUALITY'),
  treeRow({ label: 'Security', glyph: true }),
  treeRow({ label: 'Test Quality', glyph: true }),
  treeRow({ label: 'Audits & Checks', glyph: true }),
].join('');

// Level 3 — Prompt catalogue (RUNNER / REVIEW sections).
const RAIL_L3 = [
  sectionHeader('RUNNER'),
  treeRow({ label: 'Coding agent', glyph: true, active: true }),
  treeRow({ label: 'Planning agent', glyph: true }),
  treeRow({ label: 'Enhance prompt', glyph: true }),
  sectionHeader('REVIEW'),
  treeRow({ label: 'Code review', glyph: true }),
  treeRow({ label: 'Aspect: tests', glyph: true }),
  treeRow({ label: 'Aspect: requirements', glyph: true }),
  treeRow({ label: 'Summary protocol', glyph: true }),
].join('');

// Bespoke rails composite: Workspace-settings rail + Open-tabs list + CLI
// sessions + focused-view task rail — the surfaces that each rolled their own
// active look (two of them off-brand hard-coded colours) before this change.
function wsRail() {
  const item = (label, active) =>
    `<button class="ws-settings__rail-item${active ? ' ws-settings__rail-item--active' : ''}"${active ? ' aria-current="page"' : ''}>`
    + `<span class="ws-settings__rail-icon">▦</span><span class="ws-settings__rail-label">${label}</span></button>`;
  return `<div class="ws-settings__rail-group">Workspace</div>`
    + item('General', false) + item('Agents', true) + item('Usage caps', false) + item('Integrations', false);
}
function openTabsList() {
  const row = (label, active) =>
    `<button class="list-row list-row--interactive${active ? ' list-row--active' : ''}"${active ? ' aria-current="page"' : ''} style="padding-left:8px">`
    + `<span class="list-row__label">${label}</span></button>`;
  return sectionHeader('OPEN TABS', 3) + row('project-shell.ts', false) + row('tree-row.component.scss', true) + row('tokens-semantic.scss', false);
}
function cliSessions() {
  const row = (label, active) =>
    `<button class="session${active ? ' session--active' : ''}"${active ? ' aria-current="page"' : ''}><span class="session__id">${label}</span></button>`;
  return `<div class="sessions" style="padding:4px 6px"><ul class="session-list" style="list-style:none;margin:0;padding:0">`
    + row('claude · a1b2c3', false) + row('claude · d4e5f6', true) + row('codex · 778899', false) + `</ul></div>`;
}
function taskRail() {
  const item = (title, sub, active) =>
    `<button class="task-nav__item${active ? ' task-nav__item--active' : ''}"${active ? ' aria-current="page"' : ''}><span class="task-nav__item-title">${title}</span><span class="task-nav__item-sub">${sub}</span></button>`;
  return `<div style="display:flex;flex-direction:column;gap:6px">` + item('Overview', 'protocol + evidence', false) + item('Activity', 'chat + timeline', true) + item('Files', 'diff + results', false) + `</div>`;
}

const RAIL_BESPOKE = `
<div style="display:flex;flex-direction:column;gap:16px;align-items:stretch">
  <div><div class="bespoke-cap">Settings rail</div>${wsRail()}</div>
  <div><div class="bespoke-cap">Open-tabs list</div>${openTabsList()}</div>
  <div><div class="bespoke-cap">CLI sessions <small>(was off-brand blue)</small></div>${cliSessions()}</div>
  <div><div class="bespoke-cap">Task rail <small>(was off-brand indigo)</small></div>${taskRail()}</div>
</div>`;

// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------
function panelDoc({ theme, before, railHtml, railWidth }) {
  return `<!doctype html><html${theme === 'light' ? ' data-studio-theme="light"' : ''}>
<head><meta charset="utf-8"><style>
${TOKENS_CSS}
${COMPONENT_CSS}
${TASK_NAV_AFTER}
${before ? BEFORE_OVERRIDES : ''}
html,body{margin:0;padding:0}
.rail-wrap{ display:inline-block; padding:14px; background:var(--studio-bg-sidebar); border:1px solid var(--studio-border); border-radius:10px; }
.rail{ width:${railWidth || 320}px; display:flex; flex-direction:column; }
.bespoke-cap{ font-size:10px; font-weight:700; letter-spacing:.04em; text-transform:uppercase; color:var(--studio-fg-muted); margin-bottom:6px; }
.bespoke-cap small{ font-weight:500; text-transform:none; letter-spacing:0; }
</style></head>
<body class="studio"><div class="rail-wrap"><div class="rail">${railHtml}</div></div></body></html>`;
}

async function shootPanel(browser, opts) {
  const page = await browser.newPage({ deviceScaleFactor: 2 });
  await page.setContent(panelDoc(opts), { waitUntil: 'load' });
  const el = await page.$('.rail-wrap');
  const buf = await el.screenshot({ type: 'png' });
  await page.close();
  return 'data:image/png;base64,' + buf.toString('base64');
}

function compositeDoc({ title, subtitle, panels, panelW }) {
  const cols = panels.map(p => `
    <figure style="margin:0;display:flex;flex-direction:column;gap:8px;width:${panelW}px">
      <figcaption style="font:600 13px 'Segoe UI',system-ui,sans-serif;color:#c9d1e6">
        <span style="color:#e8ecf7">${p.caption}</span>
        <span style="color:#8b93a9;font-weight:400"> · ${p.sub}</span>
      </figcaption>
      <img src="${p.img}" style="display:block;width:${panelW}px;height:auto"/>
    </figure>`).join('');
  return `<!doctype html><html><head><meta charset="utf-8"><style>
    html,body{margin:0;background:#12141c}
    .frame{display:inline-block;padding:28px 32px;font-family:'Segoe UI',system-ui,sans-serif}
    h1{font-size:19px;color:#f2f4fb;margin:0 0 4px}
    p.sub{font-size:13px;color:#9aa2b8;margin:0 0 22px;max-width:${panelW * 3}px}
    .row{display:flex;gap:22px;align-items:flex-start;flex-wrap:nowrap}
  </style></head><body><div class="frame">
    <h1>${title}</h1><p class="sub">${subtitle}</p>
    <div class="row">${cols}</div>
  </div></body></html>`;
}

async function makeComposite(browser, { name, title, subtitle, railHtml, railWidth }) {
  const [beforeDark, afterDark, afterLight] = await Promise.all([
    shootPanel(browser, { theme: 'dark', before: true, railHtml, railWidth }),
    shootPanel(browser, { theme: 'dark', before: false, railHtml, railWidth }),
    shootPanel(browser, { theme: 'light', before: false, railHtml, railWidth }),
  ]);
  // Display each panel image at the rail width + framing so all three columns
  // fit; size the viewport wide enough that the element screenshot is not
  // clamped to a default 1280px viewport (which would clip the 3rd panel).
  const panelW = railWidth + 60;
  const viewportW = panelW * 3 + 22 * 2 + 64 + 40;
  const page = await browser.newPage({ viewport: { width: viewportW, height: 1200 }, deviceScaleFactor: 2 });
  await page.setContent(compositeDoc({
    title, subtitle, panelW,
    panels: [
      { caption: 'BEFORE', sub: 'dark · subtle grey-ish tint', img: beforeDark },
      { caption: 'AFTER', sub: 'dark · accent band + side bar', img: afterDark },
      { caption: 'AFTER', sub: 'light theme', img: afterLight },
    ],
  }), { waitUntil: 'load' });
  const el = await page.$('.frame');
  const file = path.join(OUT, `${name}.png`);
  await el.screenshot({ path: file });
  await page.close();
  console.log('  wrote', path.relative(FRONTEND, file));
}

const SUB = 'Unified active-item treatment (AGT-2010): accent-filled band + accent side bar, driven by the shared --studio-nav-active-* tokens. BEFORE re-applies the verbatim pre-change active rules on the same real compiled tokens.';

async function main() {
  fs.mkdirSync(OUT, { recursive: true });
  console.log('dart-sass', sass.info.split('\t')[1] ?? '', '· output ->', OUT);
  const browser = await chromium.launch();
  try {
    await makeComposite(browser, { name: 'level1-explorer-tree--before-after--composite-mocked', title: 'Level 1 — Explorer workspace tree (features/studio-shell · explorer-workspace-tree)', subtitle: SUB, railHtml: RAIL_L1, railWidth: 320 });
    await makeComposite(browser, { name: 'level2-project-hub-rail--before-after--composite-mocked', title: 'Level 2 — Deck rail (features/project-detail · project-shell)', subtitle: SUB, railHtml: RAIL_L2, railWidth: 320 });
    await makeComposite(browser, { name: 'level3-prompt-catalogue--before-after--composite-mocked', title: 'Level 3 — Prompt catalogue (features/orchestrator · prompt-admin-panel)', subtitle: SUB, railHtml: RAIL_L3, railWidth: 320 });
    await makeComposite(browser, { name: 'bespoke-settings-tabs-cli-task-rails--before-after--composite-mocked', title: 'Bespoke rails — Settings · Open-tabs · CLI sessions · Task rail', subtitle: SUB + ' Two of these dropped hard-coded off-brand colours (blue, indigo) that ignored the theme.', railHtml: RAIL_BESPOKE, railWidth: 300 });
  } finally {
    await browser.close();
  }
  console.log('done.');
}

main().catch((e) => { console.error(e); process.exit(1); });
