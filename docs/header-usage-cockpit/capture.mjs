// Concept-only browser captures. Never starts a backend or reads live workspace data.
// Run from any directory: node docs/header-usage-cockpit/capture.mjs
import { createRequire } from 'node:module';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import assert from 'node:assert/strict';
const root = dirname(fileURLToPath(import.meta.url));
const require = createRequire(resolve(root, '../../frontend/package.json'));
const { chromium } = require('playwright-core');
const out = join(root, 'assets');
await mkdir(out, { recursive: true });
const browser = await chromium.launch({headless: true, ...(process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? {executablePath: process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH} : {})});
const capturedAt = new Date().toISOString();
const capturedOn = capturedAt.slice(0, 10);
const captures = [];
const checks = [];
async function open({surface='header',version='after',theme='light',width=390,state='normal'}) {
  const page = await browser.newPage({viewport:{width,height:width===390?844:900},deviceScaleFactor:1,reducedMotion:'reduce',colorScheme:theme});
  const errors=[];
  page.on('pageerror', e=>errors.push(e.message));
  const url=pathToFileURL(join(root,'mockups.html'));
  url.search=new URLSearchParams({surface,version,theme,state,capture:'1'}).toString();
  const response=await page.goto(url.href,{waitUntil:'domcontentloaded',timeout:15000});
  assert(response?.ok(),`Failed navigation ${url}`);
  await page.locator('.cockpit').waitFor({state:'visible'});
  await page.evaluate(async()=>{await document.fonts.ready;await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))});
  assert.deepEqual(errors,[],'Page errors');
  return {page,errors,url:url.href};
}
async function bounds(page,width) {
  return page.evaluate((width)=>{
    const issues=[];
    if(document.documentElement.scrollWidth>width)issues.push('page overflow');
    const header=document.querySelector('.cockpit');
    if(header.scrollWidth>header.clientWidth)issues.push('header overflow');
    for(const value of document.querySelectorAll('[data-value]')){
      if(!value.getClientRects().length)continue;
      const v=value.getBoundingClientRect(),p=value.closest('.chip').getBoundingClientRect();
      if(v.left<p.left-1||v.right>p.right+1||v.top<p.top-1||v.bottom>p.bottom+1)issues.push('value outside chip: '+value.textContent);
    }
    return {issues,height:header.getBoundingClientRect().height};
  },width);
}
async function shot(page,file,meta,element=null){
  const path=join(out,file);
  await page.evaluate(async()=>{await document.fonts.ready;await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))});
  if(element)await page.locator(element).screenshot({path,animations:'disabled',caret:'hide'});
  else await page.screenshot({path,fullPage:true,animations:'disabled',caret:'hide'});
  const bytes=await readFile(path);
  captures.push({file,capturedOn,capturedAt,provenance:'authored HTML concept; illustrative data; not product acceptance',...meta,pngWidth:bytes.readUInt32BE(16),pngHeight:bytes.readUInt32BE(20),sha256:createHash('sha256').update(bytes).digest('hex')});
}
try {
  for(const surface of ['header','board','detail'])for(const width of [390,768,1440])for(const version of ['before','after'])for(const theme of ['light','dark']) {
    const meta={surface,width,version,theme,state:'normal'};
    const {page,errors}=await open(meta);
    if(version==='after'){
      const result=await bounds(page,width);
      assert.deepEqual(result.issues,[],JSON.stringify(meta));
      assert.equal(result.height,width<768?104:width<1280?48:40);
      checks.push({...meta,...result});
    }
    await shot(page,`${surface}-${width}-${version}-${theme}--mocked.png`,meta,surface==='header'?'#app':null);
    assert.deepEqual(errors,[]);
    await page.close();
  }
  for(const width of [320,479,480,767,1023,1024,1279,1280,1800,2560]) {
    const {page}=await open({width});
    const result=await bounds(page,width);
    assert.deepEqual(result.issues,[],`boundary ${width}`);
    assert.equal(result.height,width<768?104:width<1280?48:40);
    checks.push({boundary:width,...result});
    await page.close();
  }
  for(const state of ['warning','limited','budget','stale','unknown'])for(const theme of ['light','dark']){
    const meta={surface:'header',width:390,version:'after',theme,state};
    const {page}=await open(meta);
    assert.deepEqual((await bounds(page,390)).issues,[],state);
    await shot(page,`header-state-${state}-${theme}--mocked.png`,meta,'#app');
    await page.close();
  }
  for(const kind of ['Claude','cost','slots'])for(const theme of ['light','dark']){
    const meta={surface:'header',width:390,version:'after',theme,state:'normal',dialog:kind};
    const {page,errors}=await open(meta);
    const trigger=page.locator(`.chip[data-popup="${kind}"]`).first();
    await trigger.focus();await page.keyboard.press('Enter');
    assert.equal(await page.locator('dialog').evaluate(el=>el.open),true);
    assert.equal(await trigger.getAttribute('aria-expanded'),'true');
    await shot(page,`popover-${kind.toLowerCase()}-${theme}--mocked.png`,meta);
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('dialog').evaluate(el=>el.open),false);
    assert.equal(await trigger.evaluate(el=>el===document.activeElement),true);
    assert.deepEqual(errors,[]);
    checks.push({dialog:kind,theme,keyboard:'Enter opens; Escape closes; focus returns'});
    await page.close();
  }
  await writeFile(join(out,'capture-manifest.json'),JSON.stringify({schemaVersion:1,capturedOn,capturedAt,browser:await browser.version(),sourceRevision:'93d23ac43',source:'mockups.html',deviceScaleFactor:1,reducedMotion:'reduce',captures,checks},null,2)+'\n');
  console.log(JSON.stringify({captures:captures.length,checks:checks.length,pageErrors:0}));
} finally {await browser.close()}
