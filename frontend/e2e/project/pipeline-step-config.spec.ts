import * as fs from 'fs';
import * as path from 'path';
import { test, expect } from '../fixtures/dev-backend';
import type { Page } from '@playwright/test';

/**
 * Per-project pre/post pipeline-step config (this task).
 *
 * The Project Settings panel grows a "Pipeline steps" section: one row per
 * configurable step (from /api/projects/pipeline-catalogue) joined with the
 * project's current overrides. Aspect steps expose a model picker + an
 * enable toggle; tool/orchestrator gate steps expose a mode select. Each
 * control PUTs /api/projects/{name}/pipeline-step and the row re-renders
 * from the authoritative response.
 *
 * This spec drives the model picker + enable toggle for an aspect step and
 * asserts each write round-trips to project-settings.json (read back via the
 * settings projection). It runs against the dedicated Playwright project so
 * the writes never disturb a real project, and restores the step it touched
 * after each test.
 */

interface WatchPath { name: string; path: string }
interface PipelineStepCondition { when: string; value?: string | null }
interface PipelineStepSetting { enabled?: boolean | null; economyModel?: boolean | null; mode?: string | null; cliType?: string | null; model?: string | null; thinkingLevel?: string | null; prompt?: string | null; condition?: PipelineStepCondition | null }
interface ProjectSettingsProjection {
  pipelineSteps?: Record<string, PipelineStepSetting>;
  pipelineStepsByType?: Record<string, Record<string, PipelineStepSetting>>;
}

const STEP = 'aspect-code-quality';
const HAIKU = 'claude-haiku-4-5';
// The abort-review step: opt-in (default off) and the one step whose run
// condition the runtime evaluates, so it is the only row exposing the
// condition control.
const ABORT_STEP = 'post-abort-review';

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'pipeline-step-config');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pipeline-step-config');
})();

let projectName = '';
let projectSlug = '';
let backendBaseUrl = '';
let originalOverride: PipelineStepSetting | null = null;
let originalBugOverride: PipelineStepSetting | null = null;
let originalAbortOverride: PipelineStepSetting | null = null;

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
        body: JSON.stringify({
          models: [
            { id: HAIKU, label: 'Haiku 4.5', isDefault: false, multiplier: 0.2 },
          ],
          defaultModel: HAIKU,
          source: 'pipeline-config',
        }),
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
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  backendBaseUrl = devBackend.baseUrl;
  const pathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  expect(pathsResponse.ok, 'watch paths should load from the fixture-managed backend').toBe(true);
  const paths = await pathsResponse.json() as WatchPath[];
  const preferred = paths.find(p => /playwright/i.test(p.name)) ?? paths[0];
  expect(preferred, 'needs at least one watched project').toBeTruthy();
  projectName = preferred.name;
  projectSlug = slugFor(projectName);
  originalOverride = (await getStepOverride()) ?? null;
  originalBugOverride = (await getTypedOverride('bug', STEP)) ?? null;
  originalAbortOverride = (await getOverride(ABORT_STEP)) ?? null;

  await proxyBackend(page, devBackend.baseUrl);
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
});

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function enc(name: string): string {
  return encodeURIComponent(name);
}

async function getOverride(stepId: string): Promise<PipelineStepSetting | undefined> {
  const all = await backendApi<Record<string, ProjectSettingsProjection>>('/api/projects/settings');
  return all[projectName]?.pipelineSteps?.[stepId];
}

async function getStepOverride(): Promise<PipelineStepSetting | undefined> {
  return getOverride(STEP);
}

async function getTypedOverride(pipelineType: string, stepId: string): Promise<PipelineStepSetting | undefined> {
  const all = await backendApi<Record<string, ProjectSettingsProjection>>('/api/projects/settings');
  return all[projectName]?.pipelineStepsByType?.[pipelineType]?.[stepId];
}

async function setStep(body: { pipelineType?: string; stepId: string; enabled?: boolean | null; economyModel?: boolean | null; mode?: string | null; cliType?: string | null; model?: string | null; thinkingLevel?: string | null; prompt?: string | null; condition?: PipelineStepCondition | null }): Promise<void> {
  await backendApi(`/api/projects/${enc(projectName)}/pipeline-step`, {
    method: 'PUT',
    body: JSON.stringify(body),
  });
}

async function backendApi<T = unknown>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(`${backendBaseUrl}${path}`, {
    ...init,
    headers: {
      'content-type': 'application/json',
      'x-client-id': 'local-default',
      ...(init.headers ?? {}),
    },
  });
  const text = await response.text();
  if (!response.ok) {
    throw new Error(`API ${init.method ?? 'GET'} ${path} -> ${response.status} ${response.statusText}\n${text}`);
  }
  return text ? JSON.parse(text) as T : undefined as T;
}

test.afterEach(async () => {
  if (!projectName) return;
  // Restore the steps to their pre-test state (clear if they had no override).
  await setStep({
    pipelineType: 'task',
    stepId: STEP,
    enabled: originalOverride?.enabled ?? null,
    economyModel: originalOverride?.economyModel ?? null,
    cliType: originalOverride?.cliType ?? null,
    model: originalOverride?.model ?? null,
    thinkingLevel: originalOverride?.thinkingLevel ?? null,
    prompt: originalOverride?.prompt ?? null,
    mode: originalOverride?.mode ?? null,
    condition: originalOverride?.condition ?? null,
  });
  await setStep({
    pipelineType: 'bug',
    stepId: STEP,
    enabled: originalBugOverride?.enabled ?? null,
    economyModel: originalBugOverride?.economyModel ?? null,
    cliType: originalBugOverride?.cliType ?? null,
    model: originalBugOverride?.model ?? null,
    thinkingLevel: originalBugOverride?.thinkingLevel ?? null,
    prompt: originalBugOverride?.prompt ?? null,
    mode: originalBugOverride?.mode ?? null,
    condition: originalBugOverride?.condition ?? null,
  });
  await setStep({
    pipelineType: 'task',
    stepId: ABORT_STEP,
    enabled: originalAbortOverride?.enabled ?? null,
    cliType: originalAbortOverride?.cliType ?? null,
    model: originalAbortOverride?.model ?? null,
    thinkingLevel: originalAbortOverride?.thinkingLevel ?? null,
    prompt: originalAbortOverride?.prompt ?? null,
    mode: originalAbortOverride?.mode ?? null,
    condition: originalAbortOverride?.condition ?? null,
  });
});

test('pipeline: pipeline-step section renders and a per-step model change persists', async ({ page }) => {
  // Start from a clean override so the model select begins on Inherit.
  await setStep({ stepId: STEP, enabled: null, economyModel: null, cliType: null, model: null, thinkingLevel: null, prompt: null, mode: null });

  // Nav-rebuild step 2 (T5b): the pipeline-steps section moved out of Project
  // Settings onto the Pipeline rail; identical rows/controls, new mount.
  await page.goto(`/#/projects/${projectSlug}/pipeline`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();

  const row = page.getByTestId(`pipeline-step-row-${STEP}`);
  await expect(row).toBeVisible();
  await row.evaluate(el => { (el as HTMLDetailsElement).open = true; });

  const agentPicker = page.getByTestId(`pipeline-step-agent-${STEP}`);
  await expect(agentPicker).toBeVisible();
  await expect(agentPicker).toHaveAttribute('aria-label', /Model:/);

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-settings-defaults.png'), fullPage: true });

  // Pin this aspect to Claude / Haiku through the shared CLI+model picker;
  // assert it lands in settings.
  await agentPicker.click();
  await page.getByTestId(`pipeline-step-agent-picker-${STEP}-cli-claude`).click();
  await page.getByTestId(`pipeline-step-agent-picker-${STEP}-model-${HAIKU}`).click();
  await page.getByTestId(`pipeline-step-agent-picker-${STEP}-done`).click();
  await expect.poll(async () => (await getStepOverride())?.model).toBe(HAIKU);
  await expect.poll(async () => (await getStepOverride())?.cliType).toBe('claude');

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-step-model-haiku.png'), fullPage: true });
});

test('pipeline: a closed-row toggle disables a step without expanding it', async ({ page }) => {
  // Pin the initial state so this test is independent when selected with
  // --grep instead of relying on the model test that normally runs first.
  await setStep({ stepId: STEP, enabled: null });

  await page.goto(`/#/projects/${projectSlug}/pipeline`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();

  const row = page.getByTestId(`pipeline-step-row-${STEP}`);
  await expect(row).not.toHaveAttribute('open', '');

  const toggle = row.getByTestId(`pipeline-step-row-enabled-${STEP}`);
  await expect(toggle).toBeVisible();
  await expect(toggle).toBeChecked();

  await toggle.uncheck();
  await expect.poll(async () => (await getStepOverride())?.enabled).toBe(false);

  // The toggle click is self-contained and must not trigger the parent
  // <summary>; the row remains closed while reflecting the persisted state.
  await expect(row).not.toHaveAttribute('open', '');
  await expect(row).toHaveAttribute('data-enabled', 'false');

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-step-disabled.png'), fullPage: true });
});

test('pipeline: task type is first, switches catalogue, and keeps bug override isolated', async ({ page }) => {
  await setStep({ pipelineType: 'task', stepId: STEP, enabled: null });
  await setStep({ pipelineType: 'bug', stepId: STEP, enabled: null });

  await page.goto(`/#/projects/${projectSlug}/pipeline`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const type = page.getByTestId('pipeline-type-select');
  await expect(type).toHaveValue('task');
  await expect(type.locator('option')).toHaveText(['Task', 'Bug', 'Feature', 'Planning']);
  const bugCatalogue = page.waitForResponse(response =>
    response.url().includes('/api/projects/pipeline-catalogue')
    && response.url().includes('pipelineType=bug'));
  await type.selectOption('bug');
  await bugCatalogue;

  const bugToggle = page.getByTestId(`pipeline-step-row-enabled-${STEP}`);
  await expect(bugToggle).toBeChecked();
  await bugToggle.uncheck();
  await expect.poll(async () => (await getTypedOverride('bug', STEP))?.enabled).toBe(false);
  await expect.poll(async () => (await getStepOverride())?.enabled ?? null).toBe(null);

  await expect(page.getByTestId('pipeline-step-framework-post-lint-scss')).toContainText(/angular/i);
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '04-bug-type-disabled.png'), fullPage: true });
});

test('pipeline: economy recommendation opt-in persists for an aspect', async ({ page }) => {
  await setStep({ stepId: STEP, enabled: null, economyModel: null, cliType: null, model: null, thinkingLevel: null });
  await page.goto(`/#/projects/${projectSlug}/pipeline`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const row = page.getByTestId(`pipeline-step-row-${STEP}`);
  await row.locator('summary').click();
  const economy = page.getByTestId(`pipeline-step-economy-${STEP}`);
  await expect(economy).toBeVisible();
  await economy.check();
  await expect.poll(async () => (await getStepOverride())?.economyModel).toBe(true);

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '05-economy-spark-auto.png'), fullPage: true });
});

test('pipeline: abort-review exposes a run-condition control that persists', async ({ page }) => {
  // Start from a clean abort-review override (opt-out step, default on since 2026-07-05).
  await setStep({ stepId: ABORT_STEP, enabled: null, model: null, mode: null, condition: null });

  await page.goto(`/#/projects/${projectSlug}/pipeline`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();

  // The abort-review row renders (appended to the catalogue) and starts on.
  const row = page.getByTestId(`pipeline-step-row-${ABORT_STEP}`);
  await expect(row).toBeVisible();
  const toggle = page.getByTestId(`pipeline-step-row-enabled-${ABORT_STEP}`);
  await expect(toggle).toBeChecked();

  // Opting out must persist enabled=false (not merely clear the override).
  await toggle.uncheck();
  await expect.poll(async () => (await getOverride(ABORT_STEP))?.enabled).toBe(false);
  await expect(toggle).not.toBeChecked();
  await expect(toggle).toBeEnabled();

  // Re-enable so the condition control below is exercised against a live step.
  await toggle.check();
  // This catalogue step defaults on, so the backend may normalize an
  // explicit true back to no override. Either shape means effectively on.
  await expect.poll(async () => (await getOverride(ABORT_STEP))?.enabled !== false).toBe(true);
  await expect(toggle).toBeChecked();
  await expect(toggle).toBeEnabled();

  // The condition select is now live and defaults to "always" (empty value).
  await row.evaluate(el => { (el as HTMLDetailsElement).open = true; });
  const condition = page.getByTestId(`pipeline-step-condition-${ABORT_STEP}`);
  await expect(condition).toBeVisible();
  await expect(condition).toHaveValue('');

  // A non-value condition persists immediately.
  await condition.selectOption('on-nonzero-exit');
  await expect.poll(async () => (await getOverride(ABORT_STEP))?.condition?.when).toBe('on-nonzero-exit');

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '04-condition-nonzero-exit.png'), fullPage: true });

  // A value-bearing condition reveals a value input; it persists token + value.
  await condition.selectOption('task-type');
  const value = page.getByTestId(`pipeline-step-condition-value-${ABORT_STEP}`);
  await expect(value).toBeVisible();
  await value.fill('bug');
  await value.blur();
  await expect.poll(async () => (await getOverride(ABORT_STEP))?.condition?.when).toBe('task-type');
  await expect.poll(async () => (await getOverride(ABORT_STEP))?.condition?.value).toBe('bug');

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '05-condition-task-type.png'), fullPage: true });
});
