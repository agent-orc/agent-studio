import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * CLI-Usage-Interaktion: a click on a per-project usage row in the
 * CLI-Management "Usage detail" surface opens that project's Settings
 * rail (`#/projects/<slug>/settings`), while hover keeps showing the
 * compact peek. Navigation is shell-coordinated (the row only emits an
 * output; the shell owns the route change), so this spec verifies the
 * end-to-end wiring lands on `project-shell-panel-settings`.
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
    orchestratorEntries: 3,
    orchestratorLlmCalls: 128,
    totalInputTokens: 120_000,
    totalOutputTokens: 40_000,
    totalCacheReadTokens: 8_000,
    totalCacheCreationTokens: 2_000,
    estimatedApiCostUsd: 4.56,
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

  // The global workspace-settings overlay must close as we route into the
  // project shell (shell-coordinated nav, not a leaf-side hash write).
  await expect(page.getByTestId('workspace-settings-overlay')).not.toBeVisible();
  await expect(page.getByTestId('project-shell-panel-settings')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('project-settings-panel')).toBeVisible();
  expect(page.url()).toContain(`/projects/${slug}/settings`);

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-project-settings.png'), fullPage: true });
});

test('hovering a project usage row shows the compact peek without navigating', async ({ page }) => {
  const slug = slugFor(projectName);
  await stubAggregate(page, projectName);
  await page.goto('/');

  await openUsageDetail(page);

  const row = page.getByTestId(`cli-usage-detail-project-${slug}`);
  await expect(row).toBeVisible();

  await row.hover();

  // The instant HTML tooltip is the compact peek.
  const tip = page.getByTestId('cac-tooltip');
  await expect(tip).toBeVisible();
  await expect(tip).toContainText(projectName);

  // Hover alone must not navigate: the usage detail (and its overlay) stay put.
  await expect(page.getByTestId('cli-usage-detail')).toBeVisible();
  await expect(page.getByTestId('workspace-settings-overlay')).toBeVisible();

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-hover-peek.png'), fullPage: true });
});
