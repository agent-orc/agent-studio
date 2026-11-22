import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * CLI usage interaction: a click on a per-project usage row opens that
 * project's Settings panel, while hover keeps showing the compact peek.
 * The shell handles the row output, so this spec verifies the wiring
 * lands on `project-shell-panel-settings`.
 *
 * The "By project" rows are driven by the workspace token aggregate
 * (`/api/runner/token-summary-aggregate`). We stub that endpoint with a
 * single project whose name matches a real watch path so the slug
 * resolves to a mounted project shell; everything else (watch paths,
 * the workspace-settings home, the project shell) is the live app.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.CLI_USAGE_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'cli-usage-project-clickthrough');
})();

let projectName = '';

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function aggregateFixture(project: string) {
  return {
    projects: 1,
    orchestratorEntries: 4,
    orchestratorLlmCalls: 129,
    totalInputTokens: 120_010,
    totalOutputTokens: 40_000,
    totalCacheReadTokens: 8_000,
    totalCacheCreationTokens: 2_000,
    estimatedApiCostUsd: 4.57,
    allModelsPriced: true,
    byModel: [],
    byProject: [
      {
        project,
        orchestratorLlmCalls: 128,
        inputTokens: 120_000,
        outputTokens: 40_000,
        cacheReadTokens: 8_000,
        cacheCreationTokens: 2_000,
        estimatedApiCostUsd: 4.56,
      },
      {
        project: 'Unattributed one-shot jobs', orchestratorLlmCalls: 1,
        inputTokens: 10, outputTokens: 0, cacheReadTokens: 0,
        cacheCreationTokens: 0, estimatedApiCostUsd: 0.01,
      },
    ],
    fetchedAt: new Date().toISOString(),
    disclaimer: 'Mocked aggregate for the CLI-usage click-through e2e.',
  };
}

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThanOrEqual(1);
  const preferred = paths.find(p => /agent.?task|software.?studio/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
});

test('unattributed one-shot usage is visible without a project settings action', async ({ page }) => {
  await stubAggregate(page, projectName);
  await page.goto('/');
  await openUsageDetail(page);

  const row = page.getByTestId('cli-usage-detail-project-unattributed-one-shot-jobs');
  await expect(row).toBeVisible();
  await expect(row).toBeDisabled();
  await expect(row).toContainText('Unattributed one-shot jobs');
  await page.getByTestId('cli-usage-detail-projects').screenshot({
    path: path.join(SCREENSHOT_DIR, '04-unattributed-one-shot-row.png'),
  });
});

/** Stub both the fresh + cached aggregate so the "By project" row is
 *  deterministic regardless of real on-disk token logs. */
async function stubAggregate(page: import('@playwright/test').Page, project: string) {
  const body = JSON.stringify(aggregateFixture(project));
  await page.route('**/api/runner/token-summary-aggregate', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body }),
  );
  await page.route('**/api/runner/token-summary-aggregate/cached', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body }),
  );
}

/** Open the usage detail via the consolidated Settings view. AGT-2035 moved the
 *  usage-detail displays out of "Usage caps" into the single "Token usage"
 *  section, so the by-project rows now live there. */
async function openUsageDetail(page: import('@playwright/test').Page) {
  await page.getByTestId('studio-titlebar-settings').click();
  await page.getByTestId('workspace-settings-rail-tokens').click();
  await expect(page.getByTestId('token-usage-section')).toBeVisible();
  await expect(page.getByTestId('cli-usage-detail')).toBeVisible();
}

test('clicking a project usage row opens that project Settings rail', async ({ page }) => {
  const slug = slugFor(projectName);
  await stubAggregate(page, projectName);
  await page.goto('/');

  await openUsageDetail(page);

  const row = page.getByTestId(`cli-usage-detail-project-${slug}`);
  await expect(row).toBeVisible();
  await expect(row).toHaveAttribute('aria-label', `Open ${projectName} settings`);

  const projects = page.getByTestId('cli-usage-detail-projects');
  await projects.scrollIntoViewIfNeeded();
  await projects.screenshot({ path: path.join(SCREENSHOT_DIR, '01-by-project-rows.png') });

  await row.click();

  // The project settings panel opens from the workspace token usage view.
  await expect(page.getByTestId('workspace-settings-overlay')).not.toBeVisible();
  await expect(page.getByTestId('project-shell-panel-settings')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('project-settings-panel')).toBeVisible();

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-project-settings.png'), fullPage: true });
});

test('hovering a project usage row shows the compact peek without navigating', async ({ page }) => {
  const slug = slugFor(projectName);
  await stubAggregate(page, projectName);
  await page.goto('/');

  await openUsageDetail(page);

  const row = page.getByTestId(`cli-usage-detail-project-${slug}`);
  await expect(row).toBeVisible();
  const usageUrl = page.url();

  await row.hover();

  // The instant HTML tooltip is the compact peek.
  const tip = page.getByTestId('cac-tooltip');
  await expect(tip).toBeVisible();
  await expect(tip).toContainText(projectName);

  // Hover alone keeps the token usage view in place.
  await expect(page.getByTestId('cli-usage-detail')).toBeVisible();
  await expect(page.getByTestId('token-usage-section')).toBeVisible();
  expect(page.url()).toBe(usageUrl);

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-hover-peek.png'), fullPage: true });
});
