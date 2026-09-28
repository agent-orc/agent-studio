import { expect, test } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { pathToFileURL } from 'node:url';

const root = path.resolve(__dirname, '../../..');
const cases = [
  { dossier: 'delivery-chain', count: 7, first: 'first-bounce-owner' },
  { dossier: 'gates', count: 10, first: 'gate-ordering' },
] as const;

for (const entry of cases) {
  test(`${entry.dossier} shows the operator's selected decisions`, async ({ page }) => {
    const documentPath = path.join(root, 'docs', 'operations', entry.dossier, 'index.html');
    await page.goto(pathToFileURL(documentPath).href);

    const decided = page.locator('[data-decision-id][data-decision-status="decided"]');
    await expect(decided).toHaveCount(entry.count);
    for (const block of await decided.all()) {
      const selected = await block.getAttribute('data-selected-option-id');
      expect(selected).toBeTruthy();
      await expect(block.locator(`[data-option-id="${selected}"] input`)).toBeChecked();
      await expect(block.getByText('Decision, 2026-09-25, operator: A.', { exact: false }))
        .toBeVisible();
    }

    const output = process.env['JOB_RESULTS_DIR']?.trim();
    if (output) {
      fs.mkdirSync(output, { recursive: true });
      await page.locator(`[data-decision-id="${entry.first}"]`).screenshot({
        path: path.join(output, `${entry.dossier}-operator-decision.png`),
      });
    }
  });
}
