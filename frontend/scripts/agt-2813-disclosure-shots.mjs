// AGT-2813 - backend-free before/after capture of the result-header status line.
//
// Self-contained: compiles the REAL design tokens and the REAL component
// stylesheets (current working tree for "after", `git show HEAD:...` for
// "before"), mounts the exact DOM both templates render, and drives chromium
// over a data-free static page. No platform backend is started and no shared
// workspace state is touched - the banner is a pure function of its verdict
// input, so a screenshot of the markup is a screenshot of the component.
//
// Usage: node scripts/agt-2813-disclosure-shots.mjs <outDir>
import { chromium } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import * as sass from 'sass';

const outDir = process.argv[2];
if (!outDir) {
  console.error('usage: node scripts/agt-2813-disclosure-shots.mjs <outDir>');
  process.exit(2);
}
mkdirSync(outDir, { recursive: true });

const BANNER = 'src/app/features/task-detail/components/protocol-pane/protocol-verdict-banner/protocol-verdict-banner.component.scss';
const MARKER = 'src/app/components/disclosure-marker/disclosure-marker.component.scss';

function compile(scss, file) {
  return sass.compileString(scss, {
    loadPaths: [resolve('src'), resolve('.')],
    url: new URL(`file://${resolve(file)}`),
    silenceDeprecations: ['import', 'global-builtin', 'mixed-decls', 'color-functions'],
  }).css;
}

function atHead(path) {
  return execFileSync('git', ['show', `HEAD:frontend/${path}`], { encoding: 'utf8', cwd: resolve('..') });
}

function readNow(path) {
  return execFileSync('cat', [path], { encoding: 'utf8' });
}

const tokensCss = compile("@use 'styles/tokens-semantic';", 'src/styles.scss');
// `:host` is Angular's shadow-boundary selector; the static harness mounts the
// same elements under plain classes, so map it 1:1.
const markerCss = compile(readNow(MARKER), MARKER)
  .replaceAll(':host(.studio-disclosure__marker--open)', '.studio-disclosure__marker--open')
  .replaceAll(':host', '.studio-disclosure__marker');
const afterCss = compile(readNow(BANNER), BANNER).replaceAll(':host', '.banner-host');
const beforeCss = compile(atHead(BANNER), BANNER).replaceAll(':host', '.banner-host');

const LABEL = 'Pipeline failure';
const DETAIL = '1 pipeline step failed: core-agent-run. The runner finalized the attempt as failed and the acceptance rail left the task for human review.';
const SIGNALS = [
  ['runner', 'failed', 'Pipeline failure', '1 pipeline step failed: core-agent-run.'],
  ['review', 'succeeded', 'Review accepted', 'Accepted.'],
  ['status', 'needs-decision', 'Partial', 'Result: Partial.'],
];

const chevron = (size) => `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="none"`
  + ` stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"`
  + ` aria-hidden="true"><polyline points="9 6 15 12 9 18"/></svg>`;

const duration = `<span class="protocol-verdict__duration"><span class="protocol-verdict__duration-icon">&#9201;</span><span class="protocol-verdict__duration-value">6 min</span></span>`;

const signalsList = () => SIGNALS.map(([source, status, label, detail]) => `
      <li class="protocol-verdict-signals__item" data-status="${status}">
        <span class="protocol-verdict-signals__dot"></span>
        <span class="protocol-verdict-signals__source">${source}</span>
        <strong>${label}</strong>
        <span>${detail}</span>
      </li>`).join('');

/** The DOM the OLD template rendered: trailing caret + a second control row. */
function before(expanded) {
  return `
<div class="banner-host">
  <div class="protocol-verdict${expanded ? ' protocol-verdict--expanded' : ''}" data-kind="problem" data-status="failed" role="status">
    <span class="protocol-verdict__emoji">&#128308;</span>
    <span class="protocol-verdict__label">${LABEL}</span>
    <button type="button" class="protocol-verdict__detail protocol-verdict__detail--toggle" aria-expanded="${expanded}" aria-label="Toggle full reason">
      <span class="protocol-verdict__detail-text">${DETAIL}</span>
      <span class="protocol-verdict__detail-caret">${expanded ? '&#9652;' : '&#9662;'}</span>
    </button>
    ${duration}
  </div>
  <div class="protocol-verdict-signals">
    <button type="button" class="protocol-verdict-signals__toggle" aria-expanded="${expanded}">
      <span>Why this status?</span><span>${expanded ? '&#9652;' : '&#9662;'}</span>
    </button>
    ${expanded ? `<ul class="protocol-verdict-signals__list">${signalsList()}</ul>` : ''}
  </div>
</div>`;
}

/** The DOM the NEW template renders: the status line IS the control. */
function after(expanded) {
  return `
<div class="banner-host">
  <div class="protocol-verdict${expanded ? ' protocol-verdict--expanded' : ''}" data-kind="problem" data-status="failed" role="status">
    <span class="protocol-verdict__emoji">&#128308;</span>
    <button type="button" class="protocol-verdict__line" aria-expanded="${expanded}"${expanded ? ' aria-controls="protocol-verdict-signals-list"' : ''}>
      <app-disclosure-marker class="studio-disclosure__marker${expanded ? ' studio-disclosure__marker--open' : ''}" aria-hidden="true">${chevron(12)}</app-disclosure-marker>
      <span class="protocol-verdict__label">${LABEL}</span>
      <span class="protocol-verdict__detail-text">${DETAIL}</span>
    </button>
    ${duration}
  </div>
  ${expanded ? `<div class="protocol-verdict-signals"><ul class="protocol-verdict-signals__list" id="protocol-verdict-signals-list">${signalsList()}</ul></div>` : ''}
</div>`;
}

function page(css, body, theme) {
  return `<!DOCTYPE html>
<html lang="en"${theme === 'light' ? " data-studio-theme='light'" : ''}>
<head><meta charset="utf-8"><style>
${tokensCss}
${markerCss}
${css}
html, body { margin: 0; }
body {
  padding: 18px;
  width: 820px;
  background: var(--studio-bg-editor);
  color: var(--studio-fg);
  font: 13px/1.5 system-ui, "Segoe UI", sans-serif;
}
</style></head>
<body><div id="shot">${body}</div></body></html>`;
}

const shots = [
  ['before-collapsed', beforeCss, before(false)],
  ['before-expanded', beforeCss, before(true)],
  ['after-collapsed', afterCss, after(false)],
  ['after-expanded', afterCss, after(true)],
];

const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 860, height: 420 }, deviceScaleFactor: 2 });
const p = await ctx.newPage();
for (const theme of ['dark', 'light']) {
  for (const [name, css, body] of shots) {
    await p.setContent(page(css, body, theme), { waitUntil: 'load' });
    const file = join(outDir, `status-header-${name}-${theme}.png`);
    await p.locator('#shot').screenshot({ path: file });
    console.log(`wrote ${file}`);
  }
}
await browser.close();
