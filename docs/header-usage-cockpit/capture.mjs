// Offline concept captures. No application, workspace API or provider calls.
// Run from repository root: node docs/header-usage-cockpit/capture.mjs
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';
import { writeFileSync } from 'node:fs';
const dir = dirname(fileURLToPath(import.meta.url));
const require = createRequire(resolve(dir, '../../frontend/package.json'));
const { chromium } = require('playwright-core');
const browser = await chromium.launch({headless:true, ...(process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? {executablePath:process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH} : {})});
const report = { capturedAt: new Date().toISOString(), provenance:'Offline authored HTML, reconstructed before and synthetic proposed after. No product runtime claims.', frames:[], checks:[] };
try {
  for (const width of [390,1024,1728]) {
    for (const theme of ['light','dark']) {
      const page = await browser.newPage({viewport:{width,height:900},deviceScaleFactor:1,colorScheme:theme,reducedMotion:'reduce',timezoneId:'Europe/Berlin'});
      const errors=[];page.on('pageerror', e=>errors.push(e.message));
      for (const surface of ['header','board','detail']) for (const stage of ['before','after']) {
        const url=pathToFileURL(resolve(dir,'mockup.html'));url.search=new URLSearchParams({surface,stage,theme}).toString();
        await page.goto(url.href,{waitUntil:'load'});
        await page.evaluate(async()=>{await document.fonts.ready;await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));});
        if(errors.length)throw new Error(errors.join('\n'));
        if(stage==='after') {
          const overflow=await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth);
          if(overflow)throw new Error(`Overflow: ${surface} ${width} ${theme}`);
          const headerHeight=await page.locator('header').evaluate(el=>el.getBoundingClientRect().height);
          if(headerHeight!==(width<768?49:85))throw new Error(`Header height ${headerHeight}`);
        }
        const name=`${surface}-${stage}-${width}-${theme}--mocked.png`;
        if(surface==='header')await page.locator('header').screenshot({path:resolve(dir,'assets',name),animations:'disabled'});
        else await page.screenshot({path:resolve(dir,'assets',name),fullPage:true,animations:'disabled'});
        report.frames.push(name);
      }
      for (const expanded of ['Usage']) {
        const url=pathToFileURL(resolve(dir,'mockup.html'));url.search=new URLSearchParams({surface:'board',stage:'after',theme,expanded}).toString();
        await page.goto(url.href,{waitUntil:'load'});await page.evaluate(()=>document.fonts.ready);
        if(!await page.locator('dialog').isVisible())throw new Error('Usage dialog missing');
        const name=`usage-expanded-${width}-${theme}--mocked.png`;
        await page.screenshot({path:resolve(dir,'assets',name),fullPage:true,animations:'disabled'});report.frames.push(name);
        await page.keyboard.press('Escape');
        if(await page.locator('dialog').isVisible())throw new Error('Escape did not close');
        await page.getByRole('button',{name:width<768?'Codex weekly 15 percent used. Open all usage.':'Codex, weekly 15 percent used, current 5 hour window 32 percent used. Open usage.',exact:true}).focus();
        await page.keyboard.press('Enter');await page.keyboard.press('Escape');
        if(!await page.evaluate(()=>document.activeElement?.getAttribute('data-cli')==='Codex'))throw new Error('Focus not returned');
      }
      const doc=pathToFileURL(resolve(dir,'index.html'));
      await page.goto(doc.href,{waitUntil:'load'});await page.evaluate(()=>document.fonts.ready);
      if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw new Error(`Dossier overflow ${width}`);
      if(errors.length)throw new Error(errors.join('\n'));
      report.checks.push(`${width} ${theme}: no page errors; after views and Dossier fit; header height; dialog keyboard open, Escape and focus return`);
      await page.close();
    }
  }
  writeFileSync(resolve(dir,'assets/capture-manifest.json'),JSON.stringify(report,null,2)+'\n');
  console.log(JSON.stringify({frames:report.frames.length,checks:report.checks},null,2));
} finally {await browser.close();}
