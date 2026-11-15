#!/usr/bin/env node
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('../frontend/node_modules/playwright');

async function main() {
  const [url, jar, output] = process.argv.slice(2);
  if (!url || !jar || !output) throw new Error('Usage: capture <url> <cookie-jar> <output.png>');
  const cookies = fs.readFileSync(jar, 'utf8').split('\n')
    .map(line => line.replace(/^#HttpOnly_/, ''))
    .filter(line => line && !line.startsWith('#'))
    .map(line => line.split('\t'))
    .filter(parts => parts.length === 7)
    .map(parts => ({
      name: parts[5], value: parts[6], domain: parts[0], path: parts[2],
      secure: parts[3] === 'TRUE', httpOnly: true,
      expires: Number(parts[4]) || -1,
    }));
  const browser = await chromium.launch({ headless: true, args: ['--host-resolver-rules=MAP studio.test 127.0.0.1'] });
  try {
    const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 } });
    await context.addCookies(cookies);
    const page = await context.newPage();
    await page.goto(url, { waitUntil: 'networkidle', timeout: 30000 });
    await page.screenshot({ path: output, fullPage: true });
    if (path.extname(output) !== '.png' || fs.statSync(output).size === 0)
      throw new Error('Browser capture is empty');
  } finally {
    await browser.close();
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
