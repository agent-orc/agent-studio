import * as fs from 'fs';
import * as path from 'path';
import { test, expect } from '../fixtures/dev-backend';
import type { Page } from '@playwright/test';
import { setTheme } from '../helpers/theme';

/**
 * T4a evidence (--real) — the reworked project-level Pipeline page rendered
 * against the LIVE backend with NO route mocks. The catalogue / overrides /
 * cost all come from the real stack, so this shot is labelled --real and
 * proves the redesigned panel renders with authentic data, not just a fixture.
 * Pure reads + a screenshot; it never writes, so it is safe against the
 * shared stack.
 */

interface WatchPath { name: string; path: string }
interface PipelineCatalogueResponse {
  detectedStacks?: string[];
  steps: {
    id: string;
    appliesTo?: string;
    applicable?: boolean;
    effectiveExecution?: {
      commands: { workingSubdir: string; command: string }[];
    };
  }[];
}

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'pipeline-page');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pipeline-page');
})();

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

async function proxyBackend(page: Page, baseUrl: string): Promise<void> {
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/crash-recovery/pending') {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ pending: [] }),
      });
      return;
    }
    if (/^\/api\/cli\/[^/]+\/models$/.test(url.pathname)) {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ models: [], source: 'pipeline-evidence' }),
      });
      return;
    }
    if (url.pathname === '/api/cli/quota' || url.pathname === '/api/cli/usage') {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(url.pathname.endsWith('/quota')
          ? { at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] }
          : { at: new Date().toISOString(), sessions: [] }),
      });
      return;
    }
    const response = await route.fetch({
      url: `${baseUrl}${url.pathname}${url.search}`,
      timeout: 30_000,
    });
    await route.fulfill({ response });
  });
}

test.beforeEach(async ({ page, devBackend }) => {
  await proxyBackend(page, devBackend.baseUrl);
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
});

test('pipeline page (real): reworked panel renders against the live backend', async ({ page, devBackend }) => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const pathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  expect(pathsResponse.ok, 'watch paths should load from the fixture-managed backend').toBe(true);
  const paths = await pathsResponse.json() as WatchPath[];
  const preferred = paths.find(p => /playwright/i.test(p.name)) ?? paths[0];
  expect(preferred, 'needs at least one watched project').toBeTruthy();
  const projectSlug = slugFor(preferred.name);
  const catalogueResponse = await fetch(
    `${devBackend.baseUrl}/api/projects/pipeline-catalogue?projectName=${encodeURIComponent(preferred.name)}`,
  );
  expect(catalogueResponse.ok, 'project pipeline catalogue should load from the live backend').toBe(true);
  const catalogue = await catalogueResponse.json() as PipelineCatalogueResponse;
  expect(catalogue.detectedStacks).toContain('angular');
  const stylelintStep = catalogue.steps.find(step => step.id === 'post-lint-scss');
  expect(stylelintStep).toMatchObject({ appliesTo: 'angular', applicable: true });
  expect(stylelintStep?.effectiveExecution?.commands).toContainEqual({
    workingSubdir: 'frontend',
    command: 'npx stylelint "src/**/*.scss"',
  });

  await page.setViewportSize({ width: 1440, height: 2400 });

  await page.goto(`/#/projects/${projectSlug}/pipeline`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();
  await setTheme(page, 'light');
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'light');

  // The core step is always present in the real catalogue (live id: core-agent-run).
  await expect(page.getByTestId('pipeline-step-row-core-agent-run')).toBeVisible();

  // One representative per visible kind locks the shared identity columns.
  // The badge precedes the name, and badge/name/info/toggle geometry does not
  // depend on display-name length or framework metadata in the middle lane.
  const representativeSteps = [
    'pre-loop-guard',
    'core-agent-run',
    'post-orchestrator-review',
    'post-build-test-gate',
    'aspect-requirement-fit',
  ];
  const geometry: { kindX: number; kindWidth: number; nameX: number; infoX: number; toggleX: number }[] = [];
  for (const stepId of representativeSteps) {
    const kind = page.getByTestId(`pipeline-step-kind-${stepId}`);
    const name = page.getByTestId(`pipeline-step-name-${stepId}`);
    const info = page.getByTestId(`pipeline-step-info-${stepId}`);
    const toggle = page.getByTestId(`pipeline-step-enabled-${stepId}`);
    await expect(kind).toBeVisible();
    await expect(name).toBeVisible();
    await expect(info).toBeVisible();
    await expect(toggle).toBeVisible();
    const [kindBox, nameBox, infoBox, toggleBox] = await Promise.all([
      kind.boundingBox(), name.boundingBox(), info.boundingBox(), toggle.boundingBox(),
    ]);
    expect(kindBox).not.toBeNull();
    expect(nameBox).not.toBeNull();
    expect(infoBox).not.toBeNull();
    expect(toggleBox).not.toBeNull();
    geometry.push({
      kindX: kindBox!.x,
      kindWidth: kindBox!.width,
      nameX: nameBox!.x,
      infoX: infoBox!.x,
      toggleX: toggleBox!.x,
    });
  }
  const baseline = geometry[0];
  for (const row of geometry) {
    expect(Math.abs(row.kindX - baseline.kindX)).toBeLessThanOrEqual(1);
    expect(Math.abs(row.kindWidth - baseline.kindWidth)).toBeLessThanOrEqual(1);
    expect(Math.abs(row.nameX - baseline.nameX)).toBeLessThanOrEqual(1);
    expect(Math.abs(row.infoX - baseline.infoX)).toBeLessThanOrEqual(1);
    expect(Math.abs(row.toggleX - baseline.toggleX)).toBeLessThanOrEqual(1);
    expect(row.kindX).toBeLessThan(row.nameX);
  }

  // All visible kind labels are three characters and process-only TOOL rows
  // expose neither a summary token chip nor a detailed Tokens / 90d row.
  const kindLabels = await section.locator('[data-testid^="pipeline-step-kind-"]').allTextContents();
  expect(kindLabels.length).toBeGreaterThan(0);
  expect(kindLabels.every(label => label.trim().length === 3)).toBe(true);
  await expect(page.getByTestId('pipeline-step-kind-post-orchestrator-review')).toHaveText('ORC');
  await expect(page.getByTestId('pipeline-step-kind-post-build-test-gate')).toHaveText('TOO');
  await expect(page.getByTestId('pipeline-step-tokens-post-build-test-gate')).toHaveCount(0);
  const toolRow = page.getByTestId('pipeline-step-row-post-build-test-gate');
  await toolRow.evaluate(el => { (el as HTMLDetailsElement).open = true; });
  await expect(toolRow.getByTestId('pipeline-step-setting-tokens-post-build-test-gate')).toHaveCount(0);
  await toolRow.evaluate(el => { (el as HTMLDetailsElement).open = false; });

  const stylelintRow = page.getByTestId('pipeline-step-row-post-lint-scss');
  await expect(stylelintRow).toBeVisible();
  await expect(stylelintRow).toHaveAttribute('data-applicable', 'true');
  await expect(page.getByTestId('pipeline-step-stack-post-lint-scss')).toHaveText('angular');
  await stylelintRow.evaluate(el => { (el as HTMLDetailsElement).open = true; });
  await expect(page.getByTestId('pipeline-step-commands-post-lint-scss'))
    .toContainText('cd frontend && npx stylelint "src/**/*.scss"');
  await page.getByTestId('pipeline-step-probe-post-lint-scss').click();
  const probeOutput = page.getByTestId('pipeline-step-probe-output-post-lint-scss');
  await expect(probeOutput).toBeVisible({ timeout: 120_000 });
  await expect(probeOutput.locator('pre')).not.toHaveText('');
  await probeOutput.screenshot({
    path: path.join(SCREENSHOT_DIR, 'pipeline-step-stylelint-probe-output--real.png'),
  });

  const uiGateRow = page.getByTestId('pipeline-step-row-post-ui-human-review-gate');
  await uiGateRow.evaluate(el => { (el as HTMLDetailsElement).open = true; });
  await page.getByTestId('pipeline-step-probe-post-ui-human-review-gate').click();
  const uiGateOutput = page.getByTestId('pipeline-step-probe-output-post-ui-human-review-gate');
  await expect(uiGateOutput).toHaveAttribute('data-status', 'unavailable');
  await expect(uiGateOutput.locator('pre')).toContainText('task/run context');

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-page-after-full--real.png'), fullPage: true });
  await section.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-page-after-light--real.png') });
  await setTheme(page, 'dark');
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
  await section.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-page-after-dark--real.png') });
});
