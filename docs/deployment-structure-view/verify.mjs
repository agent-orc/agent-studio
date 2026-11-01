// Browser check of the AGT-3349 Dossier: page errors, overflow, sections,
// figures, prototype controls and keyboard use, in light and dark themes.
// Run from the repository root: node docs/deployment-structure-view/verify.mjs [outDir]
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';
import { mkdirSync } from 'node:fs';

const dir = dirname(fileURLToPath(import.meta.url));
const require = createRequire(resolve(dir, '../../frontend/package.json'));
const { chromium } = require('playwright-core');
const out = process.argv[2] || process.env.JOB_RESULTS_DIR || resolve(dir, '.verify');
mkdirSync(out, { recursive: true });
const url = pathToFileURL(resolve(dir, 'index.html')).href;
const fail = m => { throw new Error(m); };
const checks = [];
const browser = await chromium.launch({ headless: true, ...(process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}) });
try {
  for (const theme of ['light', 'dark']) {
    for (const width of [390, 1440]) {
      const page = await browser.newPage({ viewport: { width, height: 1000 }, colorScheme: theme, reducedMotion: 'reduce' });
      const errors = [];
      page.on('pageerror', e => errors.push(e.message));
      page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
      await page.goto(url, { waitUntil: 'load' });
      for (const s of ['alternatives', 'recommendation', 'evidence', 'open-decisions']) {
        if (await page.locator(`[data-concept-section="${s}"]`).count() !== 1) fail(`section ${s}`);
      }
      const figures = await page.locator('svg.ds-svg').count();
      if (figures < 12) fail(`expected at least 12 renderer SVGs, got ${figures}`);
      const broken = await page.evaluate(() => [...document.images].filter(i => !i.complete || i.naturalWidth === 0).map(i => i.src));
      if (broken.length) fail('broken images ' + broken.join(', '));
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - innerWidth);
      if (overflow > 1) fail(`document overflows by ${overflow}px at ${width}`);
      // Prototype: scenario, view, level, overlay, toggle, select, keyboard.
      const proto = page.locator('#proto');
      await proto.locator('[data-control="scenario"] button[data-value="agt"]').click();
      await proto.locator('[data-control="level"] button[data-value="2"]').click();
      const vm = proto.locator('[data-id="labvm"]');
      if (await vm.getAttribute('aria-expanded') !== 'false') fail('VM should be collapsed at level 2');
      await vm.click();
      if (await proto.locator('[data-id="labvm"]').getAttribute('aria-expanded') !== 'true') fail('VM toggle');
      await proto.locator('[data-leaf="gate"]').click();
      if (!(await page.locator('#proto-side').innerText()).includes('SSH gate transport')) fail('selection panel');
      await proto.locator('[data-leaf="gate"]').focus();
      await page.keyboard.press('Escape');
      if (!(await page.locator('#proto-side').innerText()).includes('Select an instance')) fail('Escape');
      await proto.locator('[data-id="runner"]').focus();
      await page.keyboard.press('Enter');
      if (await proto.locator('[data-id="runner"]').getAttribute('aria-expanded') !== 'false') fail('Enter collapses');
      await page.keyboard.press('c');
      if (await proto.locator('[data-control="view"] button[data-value="container"]').getAttribute('aria-pressed') !== 'true') fail('key c');
      await page.keyboard.press('1');
      if (await proto.locator('[data-control="level"] button[data-value="1"]').getAttribute('aria-pressed') !== 'true') fail('key 1');
      await proto.locator('[data-control="scenario"] button[data-value="workspace"]').click();
      if (await proto.locator('[data-control="scope"]').isHidden()) fail('scope control');
      await proto.locator('[data-control="scope"] button[data-value="web"]').click();
      await proto.locator('[data-control="level"] button[data-value="3"]').click();
      if (!(await page.locator('#proto-outline').innerHTML()).includes('other project')) fail('partial picture ghosts');
      for (const s of ['simple', 'complex', 'library']) {
        await proto.locator(`[data-control="scenario"] button[data-value="${s}"]`).click();
        for (const v of ['host', 'container']) for (const l of ['1', '2', '3']) for (const o of ['health', 'version', 'drift']) {
          await proto.locator(`[data-control="view"] button[data-value="${v}"]`).click();
          await proto.locator(`[data-control="level"] button[data-value="${l}"]`).click();
          await proto.locator(`[data-control="overlay"] button[data-value="${o}"]`).click();
        }
      }
      // Collapsed summaries equal the visible children once opened (R3), checked on the AGT VM.
      await proto.locator('[data-control="scenario"] button[data-value="agt"]').click();
      await proto.locator('[data-control="view"] button[data-value="host"]').click();
      await proto.locator('[data-control="level"] button[data-value="2"]').click();
      const summary = await proto.locator('[data-id="labvm"]').getAttribute('aria-label');
      if (!/4 containers/.test(summary)) fail('VM summary: ' + summary);
      await proto.locator('[data-id="labvm"]').click();
      await proto.locator('[data-control="level"] button[data-value="3"]').click();
      const leaves = await page.evaluate(() => {
        const li = [...document.querySelectorAll('#proto-outline li')].find(x => x.querySelector(':scope > b')?.textContent === 'Home-lab VM');
        return li ? li.querySelectorAll(':scope > ul > li').length : -1;
      });
      if (leaves !== 4) fail(`VM summary says 4 containers, outline shows ${leaves} children`);
      if (errors.length) fail(errors.join('\n'));
      await proto.locator('[data-control="scenario"] button[data-value="agt"]').click();
      await page.screenshot({ path: resolve(out, `dossier-${width}-${theme}.png`), fullPage: true });
      await proto.screenshot({ path: resolve(out, `prototype-agt-${width}-${theme}.png`) });
      checks.push(`${theme} ${width}: sections, ${figures} figures, images, no overflow, prototype controls, keyboard, partial picture, summary "${summary.split(', ').slice(-2).join(', ')}" equals ${leaves} children when opened, no page errors`);
      await page.close();
    }
  }
  console.log(checks.join('\n'));
} finally { await browser.close(); }
