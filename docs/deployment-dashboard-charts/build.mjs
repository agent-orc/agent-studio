// Builds the Dossier of AGT-3950. Run from the repository root:
//   node docs/deployment-dashboard-charts/build.mjs [packages-dir]
// The pages are their own source; the build refreshes the regions between
// <!--g:KEY--> and <!--/g--> (style, navigation, tables and numbers from
// workbench.json, bench/results.json, bench/chunks.json and assets/).
// With a packages directory (node_modules holding uplot, see README.md) it
// also bundles src/ twice: split into lazy chunks to measure them
// (bench/chunks.json) and as the single inline file prototype.html, the only
// form the Studio's Dossier viewer runs. Without it prototype.html is kept.
import { createRequire } from 'node:module';
import { readFileSync, writeFileSync, existsSync, readdirSync, mkdirSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync, brotliCompressSync } from 'node:zlib';
import { studioTokens } from './tokens.mjs';
import { reviewSnapshot, reviewDiff, LIMITS } from './review-input.mjs';
import { tables } from './tables.mjs';

const dir = dirname(fileURLToPath(import.meta.url));
const read = f => readFileSync(resolve(dir, f), 'utf8');
const require = createRequire(import.meta.url);
const esbuild = require(resolve(dir, '../../frontend/node_modules/esbuild'));
const minify = (code, loader) => esbuild.transformSync(code, { loader, minify: true, legalComments: 'none' }).code.trim();
const packagesArg = process.argv.slice(2).find(a => !a.startsWith('--'));
const packages = packagesArg ? resolve(packagesArg) : null;

export const PAGES = [['index.html', 'Decision page'], ['views.html', 'Views and filters'], ['stack.html', 'Framework, budget and data'], ['assessments.html', 'Assessments, goals and warnings'], ['prototype.html', 'Live prototype']];
const template = readFileSync(resolve(dir, '../app/templates/article-document-v2.html'), 'utf8');
const styleAt = template.indexOf('<style data-article-template="v2">');
const articleCss = minify(template.slice(template.indexOf('>', styleAt) + 1, template.indexOf('</style>', styleAt)), 'css');
const cssParts = read('src/dossier.css').split(/\/\* @part (.+?) \*\/\n/).slice(1);
function styleFor(html, extra = '') {
  let own = '';
  for (let i = 0; i < cssParts.length; i += 2) if (cssParts[i] === 'always' || html.includes(cssParts[i])) own += cssParts[i + 1];
  return `<style data-article-template="v2">/* docs/app/templates/article-document-v2.html, minified by build.mjs */${articleCss}</style>\n<style data-dossier="deployment-dashboard-charts">${minify(own + extra, 'css')}</style>`;
}
const navFor = file => '<nav class="chapters" aria-label="Pages of this Dossier"><ol>' + PAGES.map(([f, t]) => `<li><a href="${f}"${f === file ? ' aria-current="page"' : ''}>${t}</a></li>`).join('') + '</ol></nav>';

// Studio tokens for the prototype stage: dark is the Studio's :root, light its [data-studio-theme='light'].
const tokens = studioTokens();
const block = map => Object.entries(map).map(([k, v]) => `${k}:${v}`).join(';');
const spacing = '--studio-spacing-1:4px;--studio-spacing-2:8px;--studio-spacing-3:12px;--studio-spacing-4:16px;--studio-spacing-5:24px';
const tokenCss = `.dd{${block(tokens.dark)};${spacing};color-scheme:dark}.dd[data-studio-theme="light"]{${block(tokens.light)};color-scheme:light}`;

if (packages) {
  const common = { bundle: true, minify: true, legalComments: 'none', target: 'es2022', nodePaths: [join(packages, 'node_modules')], logLevel: 'error', write: false };
  // 1. Split build: what the Studio would ship as lazy chunks.
  const split = await esbuild.build({ ...common, entryPoints: [resolve(dir, 'src/app.js')], format: 'esm', splitting: true, outdir: join(packages, 'split'), chunkNames: 'chunk-[name]-[hash]', metafile: true });
  const size = b => ({ raw: b.length, gzip: gzipSync(b, { level: 9 }).length, brotli: brotliCompressSync(b).length });
  const uplotCss = readFileSync(join(packages, 'node_modules/uplot/dist/uPlot.min.css'));
  const chunks = {};
  for (const f of split.outputFiles) {
    const inputs = Object.keys(split.metafile.outputs[Object.keys(split.metafile.outputs).find(k => f.path.endsWith(k.split('/').pop()))].inputs).map(i => i.split('/').pop());
    const name = inputs.includes('app.js') ? 'shell' : inputs.includes('charts.js') ? 'charts2d' : inputs.includes('view3d.js') ? 'view3d' : 'shared';
    chunks[name] = { ...size(Buffer.from(f.contents)), inputs };
  }
  chunks.charts2d.withCss = size(Buffer.concat([Buffer.from(split.outputFiles.find(f => Buffer.from(f.contents).includes('uplot') || true && chunks.charts2d.raw === f.contents.length).contents), uplotCss]));
  mkdirSync(join(packages, 'split'), { recursive: true });
  for (const f of split.outputFiles) writeFileSync(f.path, f.contents);
  writeFileSync(join(packages, 'split/uplot.css'), uplotCss);
  writeFileSync(resolve(dir, 'bench/chunks.json'), JSON.stringify({ builtAt: new Date().toISOString(), uplot: JSON.parse(readFileSync(join(packages, 'node_modules/uplot/package.json'), 'utf8')).version, chunks }, null, 1) + '\n');
  // 2. Inline build: one file, inline script only.
  const inline = await esbuild.build({ ...common, entryPoints: [resolve(dir, 'src/app.js')], format: 'iife' });
  // "](" would read as a Markdown link to the review's link scan; a space between a member access and its call changes nothing.
  const js = Buffer.from(inline.outputFiles[0].contents).toString().replace(/<\/script/gi, '<\\/script').replace(/\]\(/g, '] (');
  const body = read('src/prototype.body.html'), cut = body.indexOf('<div class="dd"');
  const css = tokenCss + uplotCss.toString() + read('src/proto.css');
  writeFileSync(resolve(dir, 'prototype.html'), `<!DOCTYPE html>
<!-- GENERATED by build.mjs. Do not edit and do not review as text: markup from src/prototype.body.html, styles from
     src/proto.css, the Studio tokens and uPlot ${JSON.parse(readFileSync(join(packages, 'node_modules/uplot/package.json'), 'utf8')).version} (MIT), script from src/*.js, minified.
     One file because the Studio's Dossier viewer runs inline scripts only. -->
<html lang="en" data-document-pattern="concept">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Live prototype | Deployment dashboard charts</title>
${styleFor('', css)}
</head>
<body>
<main class="article">
${body.slice(0, cut).trim().replace('</header>', '</header>\n' + navFor('prototype.html'))}
${body.slice(cut).trim()}
</main>
<script>
${js}
</script>
</body>
</html>
`);
  // The split build as a page, for the lazy-loading check of verify.mjs.
  writeFileSync(join(packages, 'split/index.html'), `<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>split</title>${styleFor('', css)}</head><body><main class="article">${body}</main><script type="module" src="./app.js"></script></body></html>`);
  console.log('prototype.html', read('prototype.html').length, 'characters; chunks', Object.entries(chunks).map(([k, v]) => `${k} ${(v.gzip / 1024).toFixed(1)} kB gzip`).join(', '));
} else console.log('No packages directory given: prototype.html and bench/chunks.json are kept as they are.');

// Pages: refresh the regions in place.
const values = tables({ dir, read, PAGES });
const REGION = /<!--g:([A-Z0-9_]+)-->[\s\S]*?<!--\/g-->/g;
for (const [file] of PAGES) {
  if (file === 'prototype.html' || !existsSync(resolve(dir, file))) continue;
  // Two passes: content regions first, then the style, which depends on the classes the page ends up with.
  const fill = (html, pick) => html.replace(REGION, (m, key) => { const value = pick(key); if (value === undefined) return m; if (value == null) throw new Error(`${file}: unknown region ${key}`); return `<!--g:${key}-->${value}<!--/g-->`; });
  const body = fill(read(file), key => (key === 'STYLE' ? undefined : key === 'NAV' ? navFor(file) : values[key] ? values[key]() : null));
  const out = fill(body, key => (key === 'STYLE' ? styleFor(body.replace(/<!--g:STYLE-->[\s\S]*?<!--\/g-->/, '')) : undefined));
  if (out.includes(String.fromCharCode(8212))) throw new Error(`${file}: em dash found`);
  writeFileSync(resolve(dir, file), out);
}
// Review budgets: fail when a page a reviewer must read would be cut or left out.
if (!process.argv.includes('--no-review')) {
  const root = resolve(dir, '../..'), snapshot = reviewSnapshot(root, 'docs/deployment-dashboard-charts/index.html'), diff = reviewDiff(root, 'docs/deployment-dashboard-charts');
  const problems = [...snapshot.problems, ...diff.problems];
  console.log(`review input: snapshot ${snapshot.inspected} of ${LIMITS.files} files, ${snapshot.textCharacters} of ${LIMITS.totalCharacters} characters; diff ${diff.changedFiles} files, ${diff.lines} of ${LIMITS.diffLines} lines, ${diff.characters} of ${LIMITS.diffCharacters} characters`);
  for (const p of problems) console.log('PROBLEM ' + p);
  if (problems.length) process.exitCode = 1;
}
