// Builds the self-contained index.html of the AGT-3349 Dossier.
// Run from the repository root: node docs/deployment-structure-view/build.mjs
// Inputs: src/index.template.html, src/render.js, src/scenarios.js,
// src/prototype.js, src/figure.css and the canonical article template style.
import { createRequire } from 'node:module';
import { readFileSync, writeFileSync, statSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const dir = dirname(fileURLToPath(import.meta.url));
const src = f => resolve(dir, 'src', f);
const require = createRequire(import.meta.url);
const R = require(src('render.js'));
const S = require(src('scenarios.js'));

const template = readFileSync(resolve(dir, '../app/templates/article-document-v2.html'), 'utf8');
const styleStart = template.indexOf('<style data-article-template="v2">');
const articleStyle = template.slice(styleStart, template.indexOf('</style>', styleStart) + '</style>'.length);

const figures = {
  key: ['key', { linkLevel: 'leaf', overlay: 'all' }],
  simple: ['simple', { linkLevel: 'leaf', versions: true }],
  'agt-host': ['agt', { view: 'host', level: 3, versions: true, title: 'AGT today, host view, level 3' }],
  'agt-container': ['agt', { view: 'container', level: 3, versions: true, title: 'AGT today, container view, level 3' }],
  'agt-version': ['agt', { view: 'host', level: 2, overlay: 'version', title: 'AGT today, host view, level 2, version overlay' }],
  'complex-collapsed': ['complex', { view: 'host', level: 1, title: 'Complex setup, collapsed at level 1' }],
  'complex-expanded': ['complex', { view: 'host', level: 3, toggles: { 'us-k8s': true, 'stage-k8s': true, 'dev-vm': true }, title: 'Complex setup, expanded at level 3' }],
  'complex-container': ['complex', { view: 'container', level: 2, title: 'Complex setup, container view, level 2' }],
  library: ['library', { versions: true }],
  workspace: ['workspace', { title: 'Workspace whole picture' }],
  'workspace-web': ['workspace', { scope: 'web', title: 'Project partial picture: Website', desc: 'The Website project with its Web VM, edge proxy, website and docs containers and its built-in registry. The AGT run-target agent on the same VM and the library npm channel it consumes are faded as other projects’ objects.' }],
};

const css = readFileSync(src('figure.css'), 'utf8');
const defs = '<svg width="0" height="0" style="position:absolute" aria-hidden="true" focusable="false"><defs>'
  + '<marker id="ds-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path class="ds-arrow-head" d="M0 1 L10 5 L0 9 Z"/></marker>'
  + '<pattern id="ds-hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect class="ds-hatch-bg" width="6" height="6"/><line class="ds-hatch-line" x1="0" y1="0" x2="0" y2="6"/></pattern>'
  + '</defs></svg>';

let html = readFileSync(src('index.template.html'), 'utf8');
html = html.replace('{{ARTICLE_STYLE}}', articleStyle).replace('{{FIGURE_CSS}}', css).replace('{{DEFS}}', defs);
const sizes = {};
for (const [name, [scenario, opts]] of Object.entries(figures)) {
  const r = R.render(S[scenario], { idPrefix: `fig-${name}`, ...opts });
  sizes[name] = `${r.width}x${r.height}`;
  // Keep text at 80 % or more of its drawn size; wider drawings scroll.
  const pair = name.startsWith('workspace');
  const svg = r.svg.replace(/ width="\d+" height="\d+"/, pair ? '' : ` style="min-width: ${Math.round(r.width * 0.8)}px"`);
  if (!html.includes(`{{FIG:${name}}}`)) throw new Error(`Template lacks figure ${name}`);
  html = html.replace(`{{FIG:${name}}}`, svg);
}
const renderKb = (statSync(src('render.js')).size / 1024).toFixed(1);
html = html.replaceAll('{{RENDER_KB}}', renderKb);
const script = [readFileSync(src('render.js'), 'utf8'), readFileSync(src('scenarios.js'), 'utf8'), readFileSync(src('prototype.js'), 'utf8')]
  .join('\n').replace(/<\/script/gi, '<\\/script');
html = html.replace('{{PROTO_SCRIPT}}', script);
if (/\{\{[A-Z_:a-z-]+\}\}/.test(html)) throw new Error('Unresolved placeholder: ' + html.match(/\{\{[A-Z_:a-z-]+\}\}/)[0]);
if (html.includes('—')) throw new Error('Em dash found');
writeFileSync(resolve(dir, 'index.html'), html);
console.log(JSON.stringify({ bytes: Buffer.byteLength(html), renderKb, figures: sizes }, null, 2));
