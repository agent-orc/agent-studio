// Fixed change for the fixture coding provider. Run only inside its card checkout.
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadChromium } from '../../scripts/release/product-scenario/browser.mjs';

const index = path.resolve('index.html');
const source = await readFile(index, 'utf8');
const baseline = await readFile(new URL('click-counter.html', import.meta.url), 'utf8');
const result = process.env.JOB_RESULTS_DIR;
const browser = await (await loadChromium()).launch();
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  await page.goto(pathToFileURL(index).href);
  if (result) {
    await mkdir(result, { recursive: true });
    await page.screenshot({ path: path.join(result, 'before-home.png') });
  }
  if (!source.includes('id="count"')) {
    await writeFile(index, baseline);
    const test = await readFile('app.test.mjs', 'utf8');
    await writeFile('app.test.mjs', test.replace('Hello world', 'Click Counter'));
  } else if (!source.includes('id="history"')) {
    const next = source.replace('<script>', '<h2>Recent values</h2><ol id="history"></ol>\n<script>')
      .replace("view.textContent = String(++count);", `view.textContent = String(++count);
const history = document.getElementById('history');
const item = document.createElement('li');
item.textContent = String(count);
history.prepend(item);
if (history.children.length > 10) history.lastElementChild.remove();`)
      .replace("count = 0; view.textContent", "if (!confirm('Reset the counter?')) return; count = 0; view.textContent");
    await writeFile(index, next);
  } else {
    await writeFile(index, source.replace('type="button">Reset</button>', 'type="button"><small>Reset</small></button>'));
  }
  await page.reload();
  const count = page.getByRole('button', { name: 'Count' });
  await count.click();
  assert.equal(await page.locator('#count').innerText(), '1');
  if (await page.locator('#history').count()) {
    assert.equal(await page.locator('#history li').innerText(), '1');
    page.once('dialog', dialog => dialog.accept());
  }
  await page.getByRole('button', { name: 'Reset' }).click();
  assert.equal(await page.locator('#count').innerText(), '0');
  if (result) await page.screenshot({ path: path.join(result, 'after-home.png') });
} finally {
  await browser.close();
}
