#!/usr/bin/env node
// AGT-4030 Dossier inventory: counts shared components, per-feature controls and
// their divergence in the Studio frontend. Read-only. Run from the repository root:
//   node docs/look-and-feel-overhaul/inventory/build-inventory.mjs
// Writes docs/look-and-feel-overhaul/inventory/inventory.json next to this file.
// Every number in the Dossier comes from that JSON; the heuristics are listed in
// HEURISTICS below so a reader can argue with a rule instead of with a number.
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = join(HERE, '..', '..', '..');
const FE = join(REPO, 'frontend');
const git = (...args) => execFileSync('git', args, { cwd: REPO, encoding: 'utf8', maxBuffer: 64 << 20 });
const tracked = git('ls-files', 'frontend/src').split('\n').filter(Boolean);
const read = f => readFileSync(join(REPO, f), 'utf8');

export const HEURISTICS = {
  button: 'a <button> element in a template; its family is its first class token',
  table: 'a <table> element, or role="grid"/"table" on a non-table element',
  menu: 'an element whose class ends in menu, dropdown, popover or flyout (not <app-menu>), or a (contextmenu) handler',
  dialog: 'role="dialog"/"alertdialog" or <dialog> in a template that is not the shared dialog or side sheet',
  input: '<input>, <select>, <textarea> elements; SCSS rules whose last compound targets input/select/textarea or a class ending in input/field/select',
  chip: 'an element whose class token ends in pill, chip, badge or tag (optionally with a BEM modifier), outside the shared components',
  toolbar: 'role="toolbar", or a container element whose class ends in toolbar, actions, action-bar or controls',
  header: 'a header/div element whose class ends in head or header (not app-section-header, app-pane-header, page-action-bar)',
  empty: 'an element whose class contains "empty" that is not <app-empty-state>, <app-studio-empty-state> or the empty-state mixin',
  error: 'an element whose class ends in error, failed or load-error carrying role="alert" or a message',
  layout: ':host or root rules that declare display:grid with grid-template-columns, or a max-width on a top-level wrapper',
};

// ---------- SCSS rule parser (nesting, &, comments, @-blocks) ----------
function stripComments(src) {
  let out = '', i = 0, inStr = null;
  while (i < src.length) {
    const c = src[i], n = src[i + 1];
    if (inStr) { out += c; if (c === '\\') { out += n; i += 2; continue; } if (c === inStr) inStr = null; i++; continue; }
    if (c === '"' || c === "'") { inStr = c; out += c; i++; continue; }
    if (c === '/' && n === '*') { const e = src.indexOf('*/', i + 2); i = e < 0 ? src.length : e + 2; continue; }
    if (c === '/' && n === '/' && src[i - 1] !== ':') { const e = src.indexOf('\n', i); i = e < 0 ? src.length : e; continue; }
    out += c; i++;
  }
  return out;
}
function splitTop(s, sep = ',') {
  const parts = []; let depth = 0, cur = '';
  for (const ch of s) {
    if (ch === '(' || ch === '[') depth++;
    if (ch === ')' || ch === ']') depth--;
    if (ch === sep && depth === 0) { parts.push(cur); cur = ''; } else cur += ch;
  }
  parts.push(cur);
  return parts.map(p => p.trim()).filter(Boolean);
}
export function parseScss(src) {
  const text = stripComments(src);
  const rules = []; const includes = [];
  const stack = [{ selectors: [''], decls: [], mixin: null }];
  let buf = '', depth = 0, interp = 0;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (ch === '#' && text[i + 1] === '{') { interp++; buf += '#{'; i++; continue; }
    if (ch === '}' && interp > 0) { interp--; buf += '}'; continue; }
    if (ch === '{') {
      const prelude = buf.trim(); buf = '';
      const parent = stack[stack.length - 1];
      let frame;
      if (prelude.startsWith('@mixin')) frame = { selectors: [''], decls: [], mixin: prelude.split(/[\s(]/)[1] };
      else if (prelude.startsWith('@')) frame = { selectors: parent.selectors, decls: [], mixin: parent.mixin, at: true };
      else {
        const own = splitTop(prelude);
        const sels = [];
        for (const p of parent.selectors) for (const o of own) sels.push(o.includes('&') ? o.replaceAll('&', p) : (p ? `${p} ${o}` : o));
        frame = { selectors: sels, decls: [], mixin: parent.mixin };
      }
      stack.push(frame); depth++;
      continue;
    }
    if (ch === ';' || ch === '}') {
      const stmt = buf.trim(); buf = '';
      const frame = stack[stack.length - 1];
      if (stmt) {
        if (stmt.startsWith('@include')) includes.push({ name: stmt.replace('@include', '').trim().split(/[\s(;]/)[0], selectors: frame.selectors, mixin: frame.mixin });
        else if (!stmt.startsWith('@') && stmt.includes(':')) {
          const k = stmt.indexOf(':');
          frame.decls.push([stmt.slice(0, k).trim(), stmt.slice(k + 1).trim()]);
        }
      }
      if (ch === '}') {
        const done = stack.pop(); depth--;
        if (!done.at && done.decls.length) rules.push({ selectors: done.selectors, decls: done.decls, mixin: done.mixin });
        else if (done.at && done.decls.length) rules.push({ selectors: done.selectors, decls: done.decls, mixin: done.mixin });
        if (stack.length === 0) stack.push({ selectors: [''], decls: [], mixin: null });
      }
      continue;
    }
    buf += ch;
  }
  return { rules, includes };
}
const lastCompound = sel => { const parts = sel.trim().split(/\s*[\s>+~]\s*/); return parts[parts.length - 1] || ''; };
const classTokens = compound => [...compound.matchAll(/\.([A-Za-z_][\w-]*)/g)].map(m => m[1]);
const elementOf = compound => (compound.match(/^([a-z][a-z0-9-]*)/) || [])[1] || '';

// ---------- file sets ----------
const htmlFiles = tracked.filter(f => f.startsWith('frontend/src/app/') && f.endsWith('.html'));
const scssFiles = tracked.filter(f => f.endsWith('.scss'));
const tsFiles = tracked.filter(f => f.startsWith('frontend/src/app/') && f.endsWith('.ts') && !f.endsWith('.spec.ts'));
const SHARED_DIR = 'frontend/src/app/components/';
const isShared = f => f.startsWith(SHARED_DIR);
const areaOf = f => {
  const m = f.match(/^frontend\/src\/app\/features\/([^/]+)\//); if (m) return `features/${m[1]}`;
  if (isShared(f)) return 'components';
  if (f.startsWith('frontend/src/styles')) return 'styles';
  if (f.startsWith('frontend/src/app/')) return 'app-root';
  return 'other';
};

// ---------- components and selectors ----------
const components = [];
for (const f of tsFiles) {
  const src = read(f);
  if (!/@Component\s*\(/.test(src) && !/@Directive\s*\(/.test(src)) continue;
  const kind = /@Component\s*\(/.test(src) ? 'component' : 'directive';
  const sel = (src.match(/selector:\s*'([^']+)'/) || [])[1];
  if (!sel) continue;
  const tpl = (src.match(/templateUrl:\s*'([^']+)'/) || [])[1];
  const sty = [...src.matchAll(/styleUrls?:\s*\[?\s*'([^']+)'/g)].map(m => m[1]);
  const base = dirname(f);
  components.push({ file: f, kind, selector: sel, template: tpl ? join(base, tpl) : null, styles: sty.map(s => join(base, s)) });
}
const bySelector = new Map(components.flatMap(c => c.selector.split(',').map(s => [s.trim(), c])));
const tagOf = sel => (sel.match(/^([a-z][a-z0-9-]*)/) || [])[1];
const attrOf = sel => (sel.match(/\[([A-Za-z][\w-]*)\]/) || [])[1];

// ---------- template scan ----------
const tpl = new Map();
for (const f of htmlFiles) {
  const src = read(f);
  const tags = [...src.matchAll(/<([a-z][a-z0-9-]*)\b([^>]*)>/g)].map(m => ({ tag: m[1], attrs: m[2] }));
  const cls = a => { const m = a.match(/\bclass="([^"]*)"/); return m ? m[1].split(/\s+/).filter(t => t && !t.includes('{{')) : []; };
  tpl.set(f, { src, tags: tags.map(t => ({ ...t, classes: cls(t.attrs) })), lines: src.split('\n').length });
}
const usesSelector = (f, sel) => {
  const t = tpl.get(f); if (!t) return false;
  const tag = tagOf(sel), attr = attrOf(sel);
  if (tag && !attr) return t.tags.some(x => x.tag === tag);
  if (attr) return new RegExp(`[\\s\\[]${attr}[\\]=\\s>]`).test(t.src) && (!tag || t.tags.some(x => x.tag === tag));
  return false;
};

// shared component usage
const usesAny = (f, sel) => sel.split(',').some(s => usesSelector(f, s.trim()));
const shared = components.filter(c => isShared(c.file)).map(c => {
  const own = dirname(c.file) + '/';
  const users = htmlFiles.filter(f => !f.startsWith(own) && usesAny(f, c.selector));
  const tsUsers = tsFiles.filter(f => !f.startsWith(own) && read(f).includes(c.file.split('/').pop().replace('.ts', '')) && !htmlFiles.includes(f)).length;
  return { selector: c.selector, kind: c.kind, file: c.file, templateUsers: users.length, users, importedFromTs: tsUsers };
}).sort((a, b) => b.templateUsers - a.templateUsers);
// shared building blocks that live outside components/
const extraSharedSelectors = ['app-page-action-bar', 'app-loading-surface', 'app-studio-empty-state', 'app-row', 'app-sidesheet'];
const extraShared = extraSharedSelectors.map(s => bySelector.get(s)).filter(Boolean).filter(c => !isShared(c.file)).map(c => ({
  selector: c.selector, file: c.file, templateUsers: htmlFiles.filter(f => usesSelector(f, c.selector) && !f.startsWith(dirname(c.file) + '/')).length,
}));

// ---------- SCSS scan ----------
const scss = new Map();
for (const f of scssFiles) scss.set(f, parseScss(read(f)));
const mixinIncludes = {};
for (const [f, p] of scss) for (const inc of p.includes) {
  const name = inc.name.replace(/^m\./, '').replace(/^mixins\./, '');
  (mixinIncludes[name] ??= new Set()).add(f);
}

const CAT = {
  button: { cls: /(^|[-_])(btn|button)(--[\w-]+)?$/, el: /^button$/ },
  chip: { cls: /(^|[-_])(pill|chip|badge|tag)(--[\w-]+)?$/, el: null },
  input: { cls: /(^|[-_])(input|field|select|textarea)(--[\w-]+)?$/, el: /^(input|select|textarea)$/ },
  table: { cls: /(^|[-_])(table)(--[\w-]+)?$/, el: /^(table|th|td|thead)$/ },
  header: { cls: /(^|[-_])(head|header)(--[\w-]+)?$/, el: null },
  title: { cls: /(^|[-_])(title)(--[\w-]+)?$/, el: /^(h2|h3|h4)$/ },
  menu: { cls: /(^|[-_])(menu|dropdown|popover|flyout)(--[\w-]+)?$/, el: null },
  dialog: { cls: /(^|[-_])(modal|dialog|overlay|backdrop|scrim|drawer)(--[\w-]+)?$/, el: null },
  empty: { cls: /empty/, el: null },
  toolbar: { cls: /(^|[-_])(toolbar|actions|action-bar|controls)(--[\w-]+)?$/, el: null },
};
const PROPS = ['border-radius', 'height', 'min-height', 'font-size', 'padding', 'font-weight'];
const catRules = Object.fromEntries(Object.keys(CAT).map(k => [k, []]));
for (const [f, p] of scss) {
  for (const r of p.rules) for (const sel of r.selectors) {
    const comp = lastCompound(sel).replace(/:[\w-]+(\([^)]*\))?/g, '');
    const el = elementOf(comp), toks = classTokens(comp);
    for (const [k, c] of Object.entries(CAT)) {
      const hit = (c.el && c.el.test(el)) || toks.some(t => c.cls.test(t));
      if (hit) catRules[k].push({ file: f, selector: sel.trim(), decls: r.decls, mixin: r.mixin, pseudo: /:(hover|focus|active|disabled|focus-visible)/.test(lastCompound(sel)) });
    }
  }
}
function divergence(rules) {
  const vals = Object.fromEntries(PROPS.map(p => [p, new Map()]));
  for (const r of rules) {
    if (r.pseudo) continue;
    for (const [k, v] of r.decls) if (vals[k]) {
      const norm = v.replace(/\s*!important/, '').replace(/\s+/g, ' ');
      vals[k].set(norm, (vals[k].get(norm) || 0) + 1);
    }
  }
  return Object.fromEntries(Object.entries(vals).map(([k, m]) => [k, { distinct: m.size, top: [...m.entries()].sort((a, b) => b[1] - a[1]).slice(0, 8) }]));
}

// ---------- template-level pattern counts ----------
const patterns = {
  buttons: { elements: 0, files: new Set(), families: new Map(), noClass: 0 },
  tables: { files: new Map() },
  menus: { bespokeFiles: new Map(), contextMenuFiles: [], appMenuFiles: [] },
  dialogs: { bespokeFiles: [], appDialogFiles: [], sidesheetFiles: [] },
  inputs: { byType: new Map(), files: new Set(), selects: 0, textareas: 0, nativeCheckbox: new Set() },
  chips: { bespokeFiles: new Map(), statusIndicatorFiles: [] },
  toolbars: { roleToolbar: [], classFiles: new Map(), pageActionBar: [] },
  headers: { bespokeFiles: new Map(), sectionHeader: [], paneHeader: [], headingLevels: new Map() },
  empty: { bespokeFiles: new Map(), emptyState: [], studioEmpty: [] },
  errors: { bespokeFiles: new Map() },
};
const inc = (map, k, n = 1) => map.set(k, (map.get(k) || 0) + n);
for (const [f, t] of tpl) {
  if (isShared(f)) continue;
  for (const x of t.tags) {
    const last = x.classes;
    if (x.tag === 'button') {
      patterns.buttons.elements++; patterns.buttons.files.add(f);
      if (last.length) inc(patterns.buttons.families, last[0]); else patterns.buttons.noClass++;
    }
    if (x.tag === 'table' || /role="(grid|table)"/.test(x.attrs)) inc(patterns.tables.files, f);
    if (x.tag === 'input') { const ty = (x.attrs.match(/type="([^"]+)"/) || [, 'text'])[1]; inc(patterns.inputs.byType, ty); patterns.inputs.files.add(f); if (ty === 'checkbox') patterns.inputs.nativeCheckbox.add(f); }
    if (x.tag === 'select') { patterns.inputs.selects++; patterns.inputs.files.add(f); }
    if (x.tag === 'textarea') { patterns.inputs.textareas++; patterns.inputs.files.add(f); }
    if (!x.tag.startsWith('app-')) {
      if (last.some(c => CAT.menu.cls.test(c)) && !/trigger|btn|button/.test(last.join(' '))) inc(patterns.menus.bespokeFiles, f);
      if (last.some(c => CAT.chip.cls.test(c))) inc(patterns.chips.bespokeFiles, f);
      if (/role="toolbar"/.test(x.attrs)) patterns.toolbars.roleToolbar.push(f);
      else if (['div', 'nav', 'header', 'footer', 'section', 'menu'].includes(x.tag) && last.some(c => CAT.toolbar.cls.test(c))) inc(patterns.toolbars.classFiles, f);
      if (['header', 'div'].includes(x.tag) && last.some(c => CAT.header.cls.test(c))) inc(patterns.headers.bespokeFiles, f);
      if (last.some(c => /empty/.test(c))) inc(patterns.empty.bespokeFiles, f);
      if (last.some(c => /(^|[-_])(error|failed|load-error)(--[\w-]+)?$/.test(c))) inc(patterns.errors.bespokeFiles, f);
    }
    if (/^h[1-6]$/.test(x.tag)) inc(patterns.headers.headingLevels, x.tag);
  }
  if (/\(contextmenu\)/.test(t.src)) patterns.menus.contextMenuFiles.push(f);
  if (usesSelector(f, 'app-menu')) patterns.menus.appMenuFiles.push(f);
  const dlg = /role="(dialog|alertdialog)"|<dialog\b/.test(t.src);
  if (usesSelector(f, 'app-dialog')) patterns.dialogs.appDialogFiles.push(f);
  if (usesSelector(f, 'app-sidesheet')) patterns.dialogs.sidesheetFiles.push(f);
  if (dlg && !usesSelector(f, 'app-dialog') && !usesSelector(f, 'app-sidesheet')) patterns.dialogs.bespokeFiles.push(f);
  if (usesSelector(f, 'app-status-indicator')) patterns.chips.statusIndicatorFiles.push(f);
  if (usesSelector(f, 'app-page-action-bar')) patterns.toolbars.pageActionBar.push(f);
  if (usesSelector(f, 'app-section-header')) patterns.headers.sectionHeader.push(f);
  if (usesSelector(f, 'app-pane-header')) patterns.headers.paneHeader.push(f);
  if (usesSelector(f, 'app-empty-state')) patterns.empty.emptyState.push(f);
  if (usesSelector(f, 'app-studio-empty-state')) patterns.empty.studioEmpty.push(f);
}

// layout: per-file page grids and max-width wrappers
const layout = { hostGrid: [], maxWidth: [], splitter: htmlFiles.filter(f => usesSelector(f, '[appResizableSplitter]')) };
for (const [f, p] of scss) {
  if (!f.startsWith('frontend/src/app/')) continue;
  for (const r of p.rules) {
    const tops = r.selectors.filter(s => /^:host\b/.test(s.trim()) || s.trim().split(/\s+/).length === 1);
    if (!tops.length) continue;
    const d = Object.fromEntries(r.decls);
    if (/grid/.test(d.display || '') && d['grid-template-columns']) layout.hostGrid.push({ file: f, selector: tops[0], columns: d['grid-template-columns'] });
    if (d['max-width'] && /(px|rem|ch|em)/.test(d['max-width']) && /^:host$|__page$|-page$|__body$|-view$|__view$/.test(tops[0].trim())) layout.maxWidth.push({ file: f, selector: tops[0], value: d['max-width'] });
  }
}

// ---------- type: font families and sizes across all SCSS ----------
const typeFacts = { fontFamilies: new Map(), fontSizes: new Map(), radii: new Map(), woff2: tracked.filter(f => f.endsWith('.woff2')).length };
for (const [f, p] of scss) for (const r of p.rules) for (const [k, v] of r.decls) {
  if (k === 'font-family' && !/^(inherit|var\()/.test(v)) inc(typeFacts.fontFamilies, v.replace(/\s+/g, ' '));
  if (k === 'font-size') inc(typeFacts.fontSizes, v.replace(/\s+/g, ' '));
  if (k === 'border-radius') inc(typeFacts.radii, v.replace(/\s+/g, ' '));
}

// ---------- global button families defined in global styles ----------
const globalButtonFamilies = ['btn', 'studio-button', 'header-btn', 'icon-btn'].map(name => ({
  name, users: htmlFiles.filter(f => tpl.get(f).tags.some(x => x.tag === 'button' && x.classes.includes(name))).length,
}));

// ---------- pages ----------
const PAGES = [
  { id: 'board', label: 'Board', roots: ['app-job-column', 'app-active-board-filters', 'app-filters-dropdown', 'app-board-group-switcher', 'app-kanban-filter-sidesheet', 'app-dossier-group-board'] },
  { id: 'task-detail', label: 'Task detail', roots: ['app-job-detail'] },
  { id: 'explorer', label: 'Explorer sidebar', roots: ['app-explorer-workspace-tree', 'app-studio-activity-bar'] },
  { id: 'header-status', label: 'Header and status bar', roots: ['app-studio-navigation-header', 'app-status-bar', 'app-account-control', 'app-attention-control', 'app-task-defaults-control'] },
  { id: 'orchestrator-sheet', label: 'Orchestrator side sheet', roots: ['app-orchestrator-side-sheet-host'] },
  { id: 'dossier-viewer', label: 'Dossier viewer', roots: ['app-workbench-tab-host'] },
  { id: 'deck-overview', label: 'Deck: overview', roots: ['app-project-overview-dashboard'] },
  { id: 'deck-settings', label: 'Deck: project settings', roots: ['app-project-settings-panel'] },
  { id: 'deck-wiki', label: 'Deck: wiki', roots: ['app-project-wiki-section'] },
  { id: 'deck-git', label: 'Deck: git and integration', roots: ['app-project-git-panel', 'app-project-integration-panel'] },
  { id: 'deck-quality', label: 'Deck: quality and tests', roots: ['app-quality-studio-panel', 'app-project-test-runs-panel', 'app-regression-radar'] },
  { id: 'deck-tokens', label: 'Deck: token usage', roots: ['app-project-token-usage-panel', 'app-token-analysis-view'] },
  { id: 'deck-deploy', label: 'Deck: deploy and environments', roots: ['app-project-deploy-area', 'app-project-environment-overview'] },
  { id: 'deck-security', label: 'Deck: security', roots: ['app-security-panel'] },
  { id: 'deck-proposals', label: 'Deck: proposals', roots: ['app-project-proposals-panel'] },
  { id: 'structure', label: 'Structure', roots: ['app-structure-view'] },
  { id: 'deployments', label: 'Deployment dashboard', roots: ['app-workspace-deployment-dashboard'] },
  { id: 'usage', label: 'Usage detail', roots: ['app-usage-detail-surface'] },
  { id: 'team', label: 'Team', roots: ['app-persona-team-view'] },
  { id: 'magazine', label: 'Magazine', roots: ['app-magazine-view'] },
  { id: 'access', label: 'Settings: Access', roots: ['app-access-area'] },
  { id: 'credentials', label: 'Settings: Credentials', roots: ['app-credential-inventory'] },
  { id: 'github', label: 'Settings: GitHub', roots: ['app-github-app-panel'] },
  { id: 'secrets', label: 'Settings: Secrets', roots: ['app-secrets-panel'] },
  { id: 'remote-hosts', label: 'Settings: Remote hosts', roots: ['app-remote-hosts-panel'] },
  { id: 'task-server', label: 'Settings: Task server', roots: ['app-task-server-panel'] },
  { id: 'operations', label: 'Settings: Operations', roots: ['app-operations-security', 'app-backup-status-panel'] },
  { id: 'orchestrator-settings', label: 'Settings: Orchestrator', roots: ['app-orchestrator-logic-panel', 'app-transition-rollout-panel', 'app-decision-rules-panel'] },
  { id: 'cli-admin', label: 'Settings: CLIs', roots: ['app-cli-admin-panel'] },
  { id: 'prompts', label: 'Settings: Prompts', roots: ['app-prompt-admin-panel'] },
  { id: 'retention', label: 'Settings: Retention', roots: ['app-retention-admin'] },
  { id: 'updates', label: 'Settings: Updates', roots: ['app-updates-settings'] },
  { id: 'workspaces', label: 'Settings: Workspaces', roots: ['app-workspace-management'] },
  { id: 'appearance', label: 'Settings: Appearance', roots: ['app-appearance-settings'] },
  { id: 'first-run', label: 'Settings: First run', roots: ['app-first-run-checklist'] },
];
const statusBaseline = JSON.parse(read('frontend/scripts/status-chip-baseline.json')).documented || {};
const sinceDate = '2026-08-26';
function churn(files) {
  if (!files.length) return 0;
  const out = git('log', `--since=${sinceDate}`, '--until=2026-11-24T23:59:59', '--format=%H', '--', ...files);
  return new Set(out.split('\n').filter(Boolean)).size;
}
function closure(roots) {
  const seen = new Set(); const sharedUsed = new Set(); const queue = [...roots];
  while (queue.length) {
    const s = queue.shift(); const c = bySelector.get(s);
    if (!c || seen.has(c.file)) continue;
    if (isShared(c.file)) { sharedUsed.add(s); continue; }
    seen.add(c.file);
    if (c.template && tpl.has(c.template)) {
      for (const x of tpl.get(c.template).tags) if (x.tag.startsWith('app-') && bySelector.has(x.tag)) queue.push(x.tag);
      for (const d of components) if (d.kind === 'directive' && isShared(d.file) && usesAny(c.template, d.selector)) sharedUsed.add(d.selector);
    }
  }
  return { comps: [...seen].map(f => components.find(c => c.file === f)), sharedUsed: [...sharedUsed].sort() };
}
const pages = PAGES.map(p => {
  const { comps, sharedUsed } = closure(p.roots);
  const tfiles = comps.map(c => c.template).filter(f => f && tpl.has(f));
  const sfiles = comps.flatMap(c => c.styles).filter(f => scss.has(f));
  const extraScss = [...new Set(sfiles.flatMap(f => [...read(f).matchAll(/@use\s+'([^']+)'/g)].map(m => m[1]).filter(u => u.startsWith('.')).map(u => {
    const base = join(dirname(f), u); const cands = [base + '.scss', join(dirname(base), '_' + base.split('/').pop() + '.scss')];
    return cands.find(c => scss.has(c));
  }).filter(Boolean)))];
  const allScss = [...new Set([...sfiles, ...extraScss])];
  let buttons = 0, tables = 0, inputs = 0, chips = 0, headers = 0; const btnFamilies = new Set();
  for (const f of tfiles) for (const x of tpl.get(f).tags) {
    if (x.tag === 'button') { buttons++; if (x.classes[0]) btnFamilies.add(x.classes[0]); }
    if (x.tag === 'table') tables++;
    if (['input', 'select', 'textarea'].includes(x.tag)) inputs++;
    if (!x.tag.startsWith('app-') && x.classes.some(c => CAT.chip.cls.test(c))) chips++;
    if (['header', 'div'].includes(x.tag) && x.classes.some(c => CAT.header.cls.test(c))) headers++;
  }
  const localRules = {};
  for (const k of ['button', 'input', 'table', 'chip', 'header', 'title', 'menu', 'dialog', 'empty']) localRules[k] = catRules[k].filter(r => allScss.includes(r.file) && !r.pseudo).length;
  const fontSizes = new Set(), radii = new Set();
  for (const f of allScss) for (const r of scss.get(f).rules) for (const [k, v] of r.decls) { if (k === 'font-size') fontSizes.add(v); if (k === 'border-radius') radii.add(v); }
  const hex = allScss.reduce((n, f) => n + (read(f).replace(/\/\*[\s\S]*?\*\//g, '').match(/#[0-9a-fA-F]{3,8}\b/g) || []).length, 0);
  const baselineEntries = tfiles.filter(f => statusBaseline[relative('frontend', f)]).length;
  const tplLines = tfiles.reduce((n, f) => n + tpl.get(f).lines, 0);
  const scssLines = allScss.reduce((n, f) => n + read(f).split('\n').length, 0);
  return {
    id: p.id, label: p.label, roots: p.roots, components: comps.length, templateLines: tplLines, scssLines,
    buttons, buttonFamilies: btnFamilies.size, tables, inputs, bespokeChips: chips, bespokeHeaders: headers,
    distinctFontSizes: fontSizes.size, distinctRadii: radii.size, hexColours: hex, statusChipBaselineFiles: baselineEntries,
    localControlRules: localRules, sharedUsed, commitsSince: churn([...tfiles, ...allScss, ...comps.map(c => c.file)]),
    files: { templates: tfiles, styles: allScss },
  };
});

// ---------- result ----------
const toObj = m => Object.fromEntries([...m.entries()].sort((a, b) => b[1] - a[1]));
const result = {
  generatedFrom: git('rev-parse', 'HEAD').trim(),
  generatedAt: new Date().toISOString(),
  churnWindow: { since: sinceDate, until: '2026-11-24' },
  heuristics: HEURISTICS,
  totals: {
    templates: htmlFiles.length, scssFiles: scssFiles.length,
    components: components.filter(c => c.kind === 'component').length,
    sharedComponents: components.filter(c => isShared(c.file) && c.kind === 'component').length,
    sharedDirectives: components.filter(c => isShared(c.file) && c.kind === 'directive').length,
    featureFolders: new Set(tracked.map(areaOf).filter(a => a.startsWith('features/'))).size,
  },
  shared, extraShared,
  mixinIncludes: Object.fromEntries(Object.entries(mixinIncludes).map(([k, v]) => [k, [...v].sort()]).sort((a, b) => b[1].length - a[1].length)),
  globalButtonFamilies,
  type: { woff2Files: typeFacts.woff2, fontFamilyValues: typeFacts.fontFamilies.size, fontFamilies: toObj(typeFacts.fontFamilies), fontSizeValues: typeFacts.fontSizes.size, topFontSizes: [...typeFacts.fontSizes.entries()].sort((a, b) => b[1] - a[1]).slice(0, 15), radiusValues: typeFacts.radii.size, topRadii: [...typeFacts.radii.entries()].sort((a, b) => b[1] - a[1]).slice(0, 12) },
  patterns: {
    buttons: { elements: patterns.buttons.elements, files: patterns.buttons.files.size, distinctFamilies: patterns.buttons.families.size, withoutClass: patterns.buttons.noClass, topFamilies: [...patterns.buttons.families.entries()].sort((a, b) => b[1] - a[1]).slice(0, 40) },
    tables: { files: toObj(patterns.tables.files), fileCount: patterns.tables.files.size, elements: [...patterns.tables.files.values()].reduce((a, b) => a + b, 0) },
    menus: { bespokeFiles: toObj(patterns.menus.bespokeFiles), contextMenuFiles: patterns.menus.contextMenuFiles, appMenuFiles: patterns.menus.appMenuFiles },
    dialogs: patterns.dialogs,
    inputs: { byType: toObj(patterns.inputs.byType), selects: patterns.inputs.selects, textareas: patterns.inputs.textareas, files: patterns.inputs.files.size, nativeCheckboxFiles: patterns.inputs.nativeCheckbox.size },
    chips: { bespokeFiles: toObj(patterns.chips.bespokeFiles), bespokeElements: [...patterns.chips.bespokeFiles.values()].reduce((a, b) => a + b, 0), statusIndicatorFiles: patterns.chips.statusIndicatorFiles.length },
    toolbars: { roleToolbar: patterns.toolbars.roleToolbar, classFiles: toObj(patterns.toolbars.classFiles), pageActionBar: patterns.toolbars.pageActionBar },
    headers: { bespokeFiles: toObj(patterns.headers.bespokeFiles), bespokeElements: [...patterns.headers.bespokeFiles.values()].reduce((a, b) => a + b, 0), sectionHeader: patterns.headers.sectionHeader, paneHeader: patterns.headers.paneHeader, headingLevels: toObj(patterns.headers.headingLevels) },
    empty: { bespokeFiles: toObj(patterns.empty.bespokeFiles), emptyState: patterns.empty.emptyState, studioEmpty: patterns.empty.studioEmpty },
    errors: { bespokeFiles: toObj(patterns.errors.bespokeFiles) },
    layout: { hostGridFiles: [...new Set(layout.hostGrid.map(x => x.file))], hostGrid: layout.hostGrid, maxWidth: layout.maxWidth, splitterFiles: layout.splitter },
  },
  scssRules: Object.fromEntries(Object.entries(catRules).map(([k, rs]) => [k, {
    rules: rs.filter(r => !r.pseudo).length, files: new Set(rs.map(r => r.file)).size,
    filesOutsideShared: new Set(rs.filter(r => !isShared(r.file) && !r.file.startsWith('frontend/src/styles')).map(r => r.file)).size,
    divergence: divergence(rs),
  }])),
  pages,
};
const out = join(HERE, 'inventory.json');
writeFileSync(out, JSON.stringify(result, null, 1) + '\n');
console.log(`wrote ${relative(REPO, out)}: ${result.totals.components} components, ${pages.length} pages`);
