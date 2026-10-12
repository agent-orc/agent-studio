// Quick screenshot capture for the tag manager dialog.
// Uses Playwright's chromium against ng-serve on 4021 → stable backend on 5031.
// Outputs PNGs to the job results/ folder.
import { chromium } from 'playwright';
import { mkdir } from 'node:fs/promises';
import * as path from 'node:path';

const OUT_DIR = 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/3-progress/tag-manager-dialog-under-devtools/results';
const BASE_URL = 'http://localhost:4021';
const BACKEND = 'http://127.0.0.1:5031';

async function api(path, init = {}) {
  const res = await fetch(`${BACKEND}${path}`, {
    headers: {
      'content-type': 'application/json',
      'x-client-id': 'local-default',
      ...(init.headers ?? {}),
    },
    ...init,
  });
  return res;
}

(async () => {
  await mkdir(OUT_DIR, { recursive: true });

  // Ensure a clean slate: remove any leftover test tag.
  await api('/api/tags/e2e-tagmgr-demo', { method: 'DELETE' });

  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  const page = await ctx.newPage();

  // The devtools menu lives only in the legacy header today; flip the
  // vsCodeLayout flag off before the first paint.
  await page.addInitScript(() => {
    try { localStorage.setItem('atp.flag.vsCodeLayout', '0'); } catch { /* noop */ }
  });

  console.log('opening', BASE_URL);
  await page.goto(BASE_URL, { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-testid="app-root"]', { timeout: 15000 });
  await page.waitForTimeout(800);

  // 1) Open dev-tools menu + capture menu state.
  await page.getByTestId('devtools-menu-trigger').click();
  await page.waitForTimeout(300);
  await page.screenshot({ path: path.join(OUT_DIR, '01-devtools-menu-with-tag-manager.png'), fullPage: false });

  // 2) Click Tag manager.
  await page.getByTestId('devtools-menu-item-tag-manager').click();
  await page.waitForSelector('[data-testid="tag-manager-dialog"]');
  await page.waitForSelector('[data-testid="tag-manager-list"]');
  await page.waitForTimeout(400);
  await page.screenshot({ path: path.join(OUT_DIR, '02-tag-manager-list.png'), fullPage: false });

  // 3) Open Add form, fill in fields.
  await page.getByTestId('tag-manager-add-toggle').click();
  await page.getByTestId('tag-manager-add-label').fill('Demo tag');
  await page.getByTestId('tag-manager-add-id').fill('e2e-tagmgr-demo');
  await page.getByTestId('tag-manager-add-desc').fill('Created by the tag-manager screenshot run.');
  await page.waitForTimeout(200);
  await page.screenshot({ path: path.join(OUT_DIR, '03-tag-manager-add-form.png'), fullPage: false });

  // 4) Submit, see new row.
  await page.getByTestId('tag-manager-add-submit').click();
  await page.waitForSelector('[data-testid="tag-manager-row-e2e-tagmgr-demo"]');
  await page.waitForTimeout(400);
  await page.screenshot({ path: path.join(OUT_DIR, '04-tag-manager-after-add.png'), fullPage: false });

  // 5) Begin Edit on the new row.
  await page.getByTestId('tag-manager-edit-e2e-tagmgr-demo').click();
  await page.getByTestId('tag-manager-edit-label').fill('Demo tag (renamed)');
  await page.getByTestId('tag-manager-edit-desc').fill('Edited via the inline form.');
  await page.waitForTimeout(200);
  await page.screenshot({ path: path.join(OUT_DIR, '05-tag-manager-edit-form.png'), fullPage: false });

  // 6) Save edit + capture.
  await page.getByTestId('tag-manager-edit-save').click();
  await page.waitForTimeout(500);
  await page.screenshot({ path: path.join(OUT_DIR, '06-tag-manager-after-edit.png'), fullPage: false });

  // 7) Close, open kanban filter, show the new tag is present.
  await page.getByTestId('tag-manager-close').click();
  await page.waitForTimeout(300);
  await page.getByTestId('filters-dropdown-trigger').click();
  await page.waitForTimeout(400);
  await page.screenshot({ path: path.join(OUT_DIR, '07-filter-dropdown-shows-new-tag.png'), fullPage: false });

  // 8) Re-open Tag manager, delete the demo tag, confirm.
  // The filter dropdown backdrop intercepts pointer events; close it by
  // clicking the backdrop directly before reopening the devtools menu.
  await page.getByTestId('filters-dropdown-backdrop').click();
  await page.waitForTimeout(300);
  await page.getByTestId('devtools-menu-trigger').click();
  await page.getByTestId('devtools-menu-item-tag-manager').click();
  await page.waitForSelector('[data-testid="tag-manager-row-e2e-tagmgr-demo"]');
  await page.getByTestId('tag-manager-delete-e2e-tagmgr-demo').click();
  await page.waitForSelector('[data-testid="confirm-dialog-confirm"]');
  await page.screenshot({ path: path.join(OUT_DIR, '08-delete-confirmation.png'), fullPage: false });
  await page.getByTestId('confirm-dialog-confirm').click();
  await page.waitForTimeout(500);
  await page.screenshot({ path: path.join(OUT_DIR, '09-after-delete.png'), fullPage: false });

  await browser.close();

  // Make sure the demo tag is gone server-side.
  await api('/api/tags/e2e-tagmgr-demo', { method: 'DELETE' });
  console.log('done');
})();
