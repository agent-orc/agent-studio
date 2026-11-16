import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';
import { contrastRatio } from '../helpers/contrast';
import { sampleColours, setTheme } from '../helpers/theme';

/**
 * Nav-rebuild step 2 (T5b) — relocation smoke.
 *
 * The task is a pure move (Funktions-Diff = 0): four surfaces leave Project
 * Settings / the legacy admin entry and re-home onto the new project rails +
 * the workspace Admin panel. This spec is the acceptance "e2e-Smoke": every
 * moved function is reachable at its NEW location, and the OLD location
 * (Project Settings) no longer renders it.
 *
 *   lane sort-order    Settings → Workflow rail   (project-detail-lane-sort)
 *   pipeline steps     Settings → Pipeline rail   (project-detail-pipeline)
 *   prompt-admin       (ASS-1651) → Prompts rail  (prompt-admin-panel)
 *   CLI permission     Settings → Admin / CLI     (project-detail-cli-modes)
 *
 * Runs against the dedicated Playwright project so nothing is mutated; the
 * spec only navigates and asserts presence/absence, then captures a shot of
 * each new home.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'nav-rebuild-t5b');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'nav-rebuild-t5b');
})();

let projectName = '';
let projectSlug = '';

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  const preferred = paths.find(p => /playwright/i.test(p.name)) ?? paths[0];
  expect(preferred, 'needs at least one watched project').toBeTruthy();
  projectName = preferred.name;
  projectSlug = slugFor(projectName);
});

test('Project Settings no longer hosts the relocated sections', async ({ page }) => {
  await page.goto(`/#/projects/${projectSlug}/settings`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  // The settings panel still renders (runner mode, auto-commit, etc.) ...
  await expect(page.getByTestId('project-settings-panel')).toBeVisible();

  // ... but the three moved sections are gone from this location.
  await expect(page.getByTestId('project-detail-lane-sort')).toHaveCount(0);
  await expect(page.getByTestId('project-detail-pipeline')).toHaveCount(0);
  await expect(page.getByTestId('project-detail-cli-modes')).toHaveCount(0);

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-settings-shrunk.png'), fullPage: true });

  await page.getByTestId('project-settings-review-aspects').click();
  await expect(page.getByTestId('project-detail-pipeline')).toBeVisible();
  await expect(page.getByTestId('review-aspect-applicability')).toContainText('library runs');

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-review-aspects.png'), fullPage: true });
});

test('Workflow rail hosts the per-lane sort order', async ({ page }) => {
  await page.goto(`/#/projects/${projectSlug}/workflow`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const laneSort = page.getByTestId('project-detail-lane-sort');
  await expect(laneSort).toBeVisible();
  await laneSort.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-workflow-lane-sort.png'), fullPage: true });
});

test('Pipeline rail hosts the pipeline-step config', async ({ page }) => {
  await page.goto(`/#/projects/${projectSlug}/pipeline`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const pipeline = page.getByTestId('project-detail-pipeline');
  await expect(pipeline).toBeVisible();
  await pipeline.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-pipeline-steps.png'), fullPage: true });
});

test('Prompts rail hosts the prompt-admin surface', async ({ page }) => {
  await page.goto('/');
  await setTheme(page, 'light');
  await page.goto(`/#/projects/${projectSlug}/prompts`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  await expect(page.getByTestId('prompt-admin-panel')).toBeVisible({ timeout: 10_000 });
  const promptPanel = page.getByTestId('project-shell-panel-prompts');
  const promptList = page.getByTestId('prompt-admin-list');
  const promptSplitter = page.getByTestId('prompt-admin-splitter');
  const promptDetail = page.getByTestId('prompt-admin-detail');
  await expect.poll(() => promptList.locator('[data-testid^="prompt-admin-group-"]').count()).toBeGreaterThan(0);
  await expect.poll(() => promptList.locator('[data-testid^="prompt-admin-item-"]').count()).toBeGreaterThan(0);
  await expect(promptList).not.toContainText('shipped');
  await expect(promptList).toContainText(/\d+ overridden/);
  await expect(promptList).toContainText(/\d+ inherited/);
  const overrideIcon = promptList.locator('[data-testid^="prompt-admin-override-"]').first();
  await expect(overrideIcon).toBeVisible();
  await expect(overrideIcon).toContainText('Override');
  await overrideIcon.hover();
  await expect(page.getByTestId('cac-tooltip').locator('.cac-tooltip__body')).toContainText('Local override active');
  const firstGroup = promptList.locator('[data-testid^="prompt-admin-group-"]').first();
  await expect(firstGroup).toHaveAttribute('aria-expanded', 'true');
  await firstGroup.click();
  await expect(firstGroup).toHaveAttribute('aria-expanded', 'false');
  await expect(page.getByTestId('prompt-admin-item-commit-message.md')).toBeHidden();
  await firstGroup.click();
  await expect(firstGroup).toHaveAttribute('aria-expanded', 'true');

  await expect(promptSplitter).toBeVisible();
  await expect(promptSplitter).toHaveAttribute('role', 'separator');
  const panelBox = await promptPanel.boundingBox();
  const listBox = await promptList.boundingBox();
  const splitterBox = await promptSplitter.boundingBox();
  const detailBox = await promptDetail.boundingBox();
  expect(panelBox && listBox && splitterBox && detailBox).toBeTruthy();
  expect(Math.abs(listBox!.x - panelBox!.x), 'prompt list is flush to the project panel').toBeLessThanOrEqual(1);
  expect(Math.abs(splitterBox!.x - (listBox!.x + listBox!.width)), 'splitter abuts prompt list').toBeLessThanOrEqual(1);
  expect(Math.abs(detailBox!.x - (splitterBox!.x + splitterBox!.width)), 'detail abuts splitter').toBeLessThanOrEqual(1);

  await page.mouse.move(splitterBox!.x + splitterBox!.width / 2, splitterBox!.y + splitterBox!.height / 2);
  await page.mouse.down();
  await page.mouse.move(splitterBox!.x + splitterBox!.width / 2 + 72, splitterBox!.y + splitterBox!.height / 2, { steps: 6 });
  await page.mouse.up();
  await expect.poll(async () => (await promptList.boundingBox())?.width ?? 0).toBeGreaterThan(listBox!.width + 24);

  const nav = await sampleColours(page, '[data-testid="prompt-admin-list"] [data-testid^="prompt-admin-item-"]');
  const editor = await sampleColours(page, '[data-testid="prompt-admin-editor"]');
  expect(contrastRatio(nav.color, nav.bg), `prompt nav contrast ${nav.color} on ${nav.bg}`).toBeGreaterThanOrEqual(4.5);
  expect(contrastRatio(editor.color, editor.bg), `prompt editor contrast ${editor.color} on ${editor.bg}`).toBeGreaterThanOrEqual(4.5);

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '04-prompts-admin-light.png'), fullPage: true });
});

test('Admin / CLI & Modelle hosts the per-project CLI permission modes', async ({ page }) => {
  // Seed the studio tab state so `projectName` is the sole active project; the
  // active-tab effect runs setSoleProject, which is what scopes the embedded
  // CLI-modes control in the Admin panel.
  await page.goto('/');
  await page.evaluate((name) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [
        { kind: 'board', projectName: '__all__', sticky: true },
        { kind: 'board', projectName: name },
      ],
      activeKey: `board:${name}`,
    }));
    localStorage.setItem('activeProjects', JSON.stringify([name]));
    location.hash = '';
  }, projectName);
  await page.reload();
  await page.waitForLoadState('domcontentloaded');

  await page.getByTestId('studio-ab-admin').click();
  const adminCliModes = page.getByTestId('studio-admin-cli-modes');
  await expect(adminCliModes).toBeVisible({ timeout: 10_000 });
  await expect(adminCliModes.getByTestId('project-detail-cli-modes')).toBeVisible();

  await adminCliModes.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '05-admin-cli-modes.png'), fullPage: true });
});
