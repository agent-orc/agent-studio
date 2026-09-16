import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'node:path';

/**
 * AGT-2812: a Dossier named in a rendered document reads as a Dossier.
 *
 * The spec injects document markup the way the microcard spec does, so it
 * exercises the real host hydrator and the real chip against a mocked Dossier
 * catalogue, without depending on a seeded repository.
 */
const results = process.env.JOB_RESULTS_DIR
  || path.resolve('test-results', 'dossier-reference-chips');

const CATALOGUE = {
  projectName: null,
  count: 2,
  currentCount: 2,
  historyCount: 0,
  items: [
    {
      projectName: 'Agent Studio',
      workbench: {
        id: 'decision-cards', key: 'AGT-W54', title: 'Decision cards', summary: '',
        status: 'decision-pending', phase: 'decision-ready',
        updatedAtUtc: '2026-09-01T00:00:00Z',
        entryPath: 'docs/operations/decision-cards/index.html',
        valid: true, error: null, sourceTaskKeys: ['AGT-2795'],
      },
    },
    {
      projectName: 'Agent Studio',
      workbench: {
        id: 'quota-probe', key: 'AGT-W60', title: 'Quota probe', summary: '',
        status: 'decided', phase: null, updatedAtUtc: '2026-09-02T00:00:00Z',
        entryPath: 'docs/quality/quota-probe/index.html',
        valid: true, error: null, sourceTaskKeys: [],
      },
    },
  ],
};

test.beforeEach(async ({ page }) => {
  mkdirSync(results, { recursive: true });
  await page.route('**/api/**', route => route.fulfill({
    contentType: 'application/json', body: '[]',
  }));
  await page.route('**/api/workbenches*', route => route.fulfill({
    contentType: 'application/json', body: JSON.stringify(CATALOGUE),
  }));
  await page.goto('/');
  await page.locator('app-root').waitFor({ state: 'attached' });
});

const DOCUMENT = `
  <main style="width:900px;margin:48px auto;padding:32px;background:var(--studio-bg-elevated);
               border:1px solid var(--studio-border);border-radius:12px;color:var(--studio-fg);
               font:15px system-ui">
    <h1 style="margin:0 0 20px">Result</h1>
    <cac-markdown>
      <p>The decision was taken in AGT-W54 and recorded in
        <a href="docs/operations/decision-cards/index.html">docs/operations/decision-cards/index.html</a>,
        whose descriptor is <code>docs/operations/decision-cards/workbench.json</code>.</p>
      <p>Unrelated: <code>docs/operations/not-a-dossier/index.html</code> and
        <code>workbench.json</code> stay plain text.</p>
      <pre><code>docs/quality/quota-probe/index.html</code></pre>
    </cac-markdown>
  </main>`;

test('a Dossier named by key, path, or descriptor renders as one chip', async ({ page }) => {
  await page.evaluate(html => {
    document.documentElement.dataset['studioTheme'] = 'light';
    document.body.innerHTML = html;
  }, DOCUMENT);

  const chips = page.getByTestId('dossier-reference-chip');
  await expect(chips).toHaveCount(3);
  await expect(chips.first()).toContainText('AGT-W54');
  await expect(chips.first()).toContainText('Decision cards');
  await expect(chips.first()).toContainText('Decision pending');

  // Unknown paths and a code sample keep their plain rendering.
  await expect(page.locator('code', { hasText: 'not-a-dossier' })).toBeVisible();
  await expect(page.locator('pre code')).toHaveText('docs/quality/quota-probe/index.html');

  await page.screenshot({
    path: path.join(results, 'dossier-reference-chips--light.png'), fullPage: true,
  });
});

test('the chip links into the Dossier view and keeps the source as secondary', async ({ page }) => {
  await page.evaluate(html => {
    delete document.documentElement.dataset['studioTheme'];
    document.body.innerHTML = html;
  }, DOCUMENT);

  const chip = page.getByTestId('dossier-reference-chip').first();
  const link = chip.getByRole('link', { name: /Open Dossier AGT-W54/ });
  await expect(link).toHaveAttribute('href', /\/workbenches\/decision-cards$/);
  await expect(chip.getByRole('button', { name: /Open source docs\/operations\/decision-cards/ }))
    .toBeVisible();

  await page.screenshot({
    path: path.join(results, 'dossier-reference-chips--dark.png'), fullPage: true,
  });
});
