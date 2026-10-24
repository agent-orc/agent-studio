import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';
import { api } from '../helpers/api';

const resultsDir = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'review-observations')
  : path.resolve('test-results', 'review-observations');

test('review observations render model rates and cited miss count in both themes', async ({ page }) => {
  const watchPaths = await api<Array<{ name: string }>>('/api/watch-paths');
  const project = watchPaths[0];
  expect(project).toBeTruthy();
  await page.route('**/token-usage/review-observations', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      project: project.name, generatedAt: '2026-10-21T12:00:00Z', method: 'Studio observations',
      reviews: [], laterEvidence: [{ id: 'e', reviewId: 'r', source: 'gate', evidenceRef: 'gate.md', summary: 'Miss' }],
      byModelAndReasoning: [{
        model: 'gpt-6-sol', reasoningLevel: 'high', reviews: 2, blockers: 1,
        approved: 1, missEligibleApprovals: 1, misses: 1, realFindings: null, costUsd: 0.4,
        costPerReview: 0.2, blockerRate: 0.5, missRate: 1, costPerRealFinding: null,
      }],
    }),
  }));
  const slug = project.name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  await page.goto(`/#/projects/${slug}/token-usage`);
  const observations = page.getByRole('region', { name: 'Review observations' });
  await expect(observations).toContainText('gpt-6-sol');
  await expect(observations).toContainText('100.0%');
  await expect(observations).toContainText('1 cited later defect link');
  mkdirSync(resultsDir, { recursive: true });
  await dismissDevErrorDialog(page);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await observations.screenshot({
      path: path.join(resultsDir, `review-observations-${theme}.png`),
    });
  }
});
