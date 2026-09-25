#!/usr/bin/env node
// Read-only navigation replay against a real Stable browser surface.
import { createRequire } from 'node:module';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
const require = createRequire(resolve('frontend/package.json'));
const { chromium } = require('playwright-core');
const out = resolve(process.env.MEASURE_OUT ?? 'docs/task-switch-performance/evidence');
const assets = resolve('docs/task-switch-performance/assets');
const origin = process.env.STABLE_FRONTEND_URL ?? 'http://127.0.0.1:4011';
await mkdir(out, { recursive: true });
await mkdir(assets, { recursive: true });
const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport:{width:1600,height:1000}, deviceScaleFactor:1, reducedMotion:'reduce' });
const page = await context.newPage();
const errors = [], pending = new Set(), samples = [], captures = [], requestEvents = [];
const requests = new Map();
const report = { startedAt:new Date().toISOString(), origin, browserVersion:browser.version(),
  browserHost:'Linux runner over forwarded connection to Windows Stable', samples, errors, captures, requestEvents };
page.on('pageerror', e => errors.push(e.message));
page.on('request', r => {
  pending.add(r.url());
  if (r.url().includes('/api/')) {
    const entry={path:new URL(r.url()).pathname,startedAt:new Date().toISOString(),method:r.method()};
    requests.set(r,entry);requestEvents.push(entry);
  }
});
page.on('response', r => {const entry=requests.get(r.request());if(entry){entry.status=r.status();entry.serverTiming=r.headers()['server-timing']??null;}});
page.on('requestfinished', r => {pending.delete(r.url());const entry=requests.get(r);if(entry)entry.timing=r.timing();});
page.on('requestfailed', r => {pending.delete(r.url());const entry=requests.get(r);if(entry)entry.failure=r.failure()?.errorText;});
await page.addInitScript(() => { performance.setResourceTimingBufferSize(10000); localStorage.setItem('perf','1'); localStorage.setItem('atp.studio.theme','light'); });
try {
  const response = await page.goto(origin + '/?perf=1&task=AGT-2910', {waitUntil:'commit',timeout:15000});
  if (!response?.ok()) throw Error(`Navigation HTTP ${response?.status()}`);
  for (let i=0;i<20;i++) {
    if (await page.getByTestId('overview-title-key').isVisible()) break;
    console.log(`Boot ${i*30}s, pending ${pending.size}: ${[...pending].slice(0,2).join(', ')}`);
    await page.waitForTimeout(30000);
  }
  if (!(await page.getByTestId('overview-title-key').isVisible())) throw Error('Stable detail did not become ready within 600 seconds');
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(2000);
  if (errors.length) throw Error('Page errors during readiness');
  report.readyAt = new Date().toISOString();
  const obstructed = await page.getByTestId('crash-recovery-prompt-overlay').isVisible();
  for (const theme of ['light','dark']) {
    await page.evaluate(t => {document.documentElement.setAttribute('data-studio-theme',t); localStorage.setItem('atp.studio.theme',t);}, theme);
    await page.emulateMedia({colorScheme:theme});
    await page.evaluate(() => new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
    const filename = `stable-${obstructed?'recovery-dialog':'task-detail'}-${theme}--real.png`;
    await page.screenshot({path:resolve(assets,filename),animations:'disabled',caret:'hide'});
    captures.push({filename,theme,at:new Date().toISOString(),url:page.url(),viewport:{width:1600,height:1000},deviceScaleFactor:1});
  }
  if (obstructed) { report.blocker='shared-crash-recovery-decision'; throw Error('Crash recovery overlay blocks real navigation; no shared recovery action was taken'); }
  for(let i=0;i<20;i++) {
    const button = page.getByTestId(i%2===0?'studio-task-prev':'studio-task-next');
    if (!(await button.isEnabled())) throw Error('Pager pair is unavailable');
    const beforeKey = await page.getByTestId('overview-title-key').innerText();
    await page.evaluate(previous => {
      performance.clearMeasures(); performance.clearMarks(); performance.clearResourceTimings();
      window.__switchSample = { previous };
      document.addEventListener('click',()=>{
        const s=window.__switchSample; s.start=performance.now();
        const check=()=>{
          const key=document.querySelector('[data-testid="overview-title-key"]')?.textContent?.trim();
          const pane=document.querySelector('[data-testid="pane-prompt-body"]');
          if(key && key!==previous.trim() && pane && pane.getBoundingClientRect().height>0) {
            requestAnimationFrame(()=>requestAnimationFrame(()=>{s.detailReadyMs=performance.now()-s.start;s.key=key;}));
          } else if(performance.now()-s.start<20000) requestAnimationFrame(check);
        };requestAnimationFrame(check);
      },{once:true,capture:true});
    },beforeKey);
    await button.click();
    await page.waitForFunction(()=>window.__switchSample?.detailReadyMs!==undefined,{},{timeout:22000});
    await page.waitForTimeout(500);
    const sample=await page.evaluate(()=>({ ...window.__switchSample,
      measures:performance.getEntriesByType('measure').map(e=>({name:e.name,durationMs:e.duration})),
      requests:performance.getEntriesByType('resource').filter(e=>e.name.includes('/api/')).map(e=>({
        path:new URL(e.name).pathname, startMs:e.startTime-window.__switchSample.start,
        durationMs:e.duration, ttfbMs:e.responseStart-e.requestStart,
        downloadMs:e.responseEnd-e.responseStart, decodedBytes:e.decodedBodySize,
        transferBytes:e.transferSize, serverTiming:e.serverTiming?.map(t=>({name:t.name,duration:t.duration}))
      }))}));
    samples.push(sample);
    console.log(`Switch ${i+1}: ${sample.detailReadyMs.toFixed(1)}ms`);
    await page.waitForTimeout(500);
  }
} catch(e) { report.failure=e.message;console.log(e.message);console.log((await page.locator('body').innerText()).slice(0,1200));process.exitCode=1; }
finally {
  report.finishedAt=new Date().toISOString();
  report.pending=[...pending].map(u=>new URL(u).pathname);
  report.bootstrapRequests=await page.evaluate(()=>performance.getEntriesByType('resource').filter(e=>e.name.includes('/api/')).map(e=>({
    path:new URL(e.name).pathname,durationMs:e.duration,transferBytes:e.transferSize,decodedBytes:e.decodedBodySize,
    serverTiming:e.serverTiming?.map(t=>({name:t.name,duration:t.duration}))
  })));
  await writeFile(resolve(out,'browser-replay.json'),JSON.stringify(report,null,2)+'\n');
  await browser.close();
}
