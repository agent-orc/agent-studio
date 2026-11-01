// Offline checks for the quality-studio-integration Dossier (AGT-3352).
// Run from the repository root: node docs/quality-studio-integration/capture/verify.mjs [previewDir]
// Checks the descriptor, local links and fragments, image files, the four
// concept sections, the verbatim article-v2 style block, and renders the
// Dossier in Chromium (light and dark, 1440 and 390 px) for page errors and
// horizontal overflow. Optional previewDir receives full-page renders.
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';
import { existsSync, readFileSync, mkdirSync } from 'node:fs';

const dir = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(dir, '../..');
const require = createRequire(resolve(repo, 'frontend/package.json'));
const { chromium } = require('playwright-core');
const failures = [];
const check = (ok, message) => { if (!ok) failures.push(message); };

const html = readFileSync(resolve(dir, 'index.html'), 'utf8');
const wb = JSON.parse(readFileSync(resolve(dir, 'workbench.json'), 'utf8'));
for (const key of ['schemaVersion', 'id', 'title', 'summary', 'entrypoint', 'status', 'phase', 'updatedAt', 'sourceTaskKeys', 'implementationTasks']) {
  check(key in wb, `workbench.json lacks ${key}`);
}
check(wb.id === 'quality-studio-integration', 'id must equal the slug');
check(wb.entrypoint === 'index.html' && wb.status === 'decision-pending', 'entrypoint/status');
check(wb.sourceTaskKeys.includes('AGT-3352'), 'sourceTaskKeys must contain AGT-3352');
check(wb.implementationTasks[0]?.title.includes('project jump'), 'first slice must be the project jump');
for (const t of wb.implementationTasks) {
  check(t.title && t.promptMarkdown && t.acceptanceScope?.criteria?.length >= 3, `slice ${t.id} incomplete`);
  check(/Agent Studio|Quality Studio/.test(t.targetProject ?? ''), `slice ${t.id} lacks a target project`);
}

const template = readFileSync(resolve(repo, 'docs/app/templates/article-document-v2.html'), 'utf8');
const style = template.match(/<style data-article-template="v2">[\s\S]*?<\/style>/)[0];
check(html.includes(style), 'article-v2 style block is not reused verbatim');
for (const section of ['alternatives', 'recommendation', 'evidence', 'open-decisions']) {
  check(html.includes(`data-concept-section="${section}"`), `missing section ${section}`);
}
check(!/[–—]/.test(html), 'en or em dash present');

const ids = new Set([...html.matchAll(/\sid="([^"]+)"/g)].map(m => m[1]));
for (const [, href] of html.matchAll(/href="([^"]+)"/g)) {
  if (href.startsWith('#')) check(ids.has(href.slice(1)), `broken fragment ${href}`);
  else if (!/^https?:/.test(href)) check(existsSync(resolve(dir, href.split('#')[0])), `broken link ${href}`);
}
for (const [, src] of html.matchAll(/<img[^>]+src="([^"]+)"/g)) check(existsSync(resolve(dir, src)), `missing image ${src}`);
for (const m of html.matchAll(/<img([^>]+)>/g)) check(/alt="[^"]{20,}"/.test(m[1]), 'image without useful alt text');

const preview = process.argv[2];
if (preview) mkdirSync(preview, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  for (const width of [1440, 390]) {
    for (const theme of ['light', 'dark']) {
      const page = await browser.newPage({ viewport: { width, height: 900 }, colorScheme: theme, reducedMotion: 'reduce' });
      const errors = [];
      page.on('pageerror', e => errors.push(e.message));
      await page.goto(pathToFileURL(resolve(dir, 'index.html')).href, { waitUntil: 'load' });
      await page.evaluate(async () => { await document.fonts.ready; });
      check(errors.length === 0, `${width} ${theme}: page errors ${errors.join('; ')}`);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - innerWidth);
      check(overflow <= 1, `${width} ${theme}: horizontal overflow ${overflow}px`);
      const broken = await page.evaluate(() => [...document.images].filter(i => !i.complete || i.naturalWidth === 0).map(i => i.src));
      check(broken.length === 0, `${width} ${theme}: images not loaded ${broken.join(', ')}`);
      if (preview) await page.screenshot({ path: resolve(preview, `dossier-${width}-${theme}.png`), fullPage: true });
      await page.close();
    }
  }
} finally {
  await browser.close();
}
if (failures.length) { console.error(failures.join('\n')); process.exit(1); }
console.log(`ok: ${wb.implementationTasks.length} slices, ${ids.size} ids, links, images, sections, style, render 1440/390 light/dark`);
