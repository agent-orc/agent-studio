import { test, expect, type Page } from '@playwright/test';
import { createJob, getJob, moveJob } from '../helpers/jobs';
import { api, BACKEND } from '../helpers/api';
import { setTheme } from '../helpers/theme';
import * as path from 'path';
import * as fs from 'fs';

const BASE = 'a'.repeat(40);
const HEAD_ONE = 'b'.repeat(40);
const HEAD_TWO = 'c'.repeat(40);
const LANDED = 'd'.repeat(40);
const shots = process.env.JOB_RESULTS_DIR
  ? path.resolve(process.env.JOB_RESULTS_DIR) : path.resolve('test-results/quality-panel');

interface WatchPath { name: string; path: string; }

function round(number: number, verdict: string, mode: 'decide' | 'shadow' = 'decide') {
  return {
    round: number, attemptId: `review-${number}`, runAttemptId: `run-${number}`,
    repositoryId: 'proj-002', baseSha: BASE, headSha: number === 1 ? HEAD_ONE : HEAD_TWO,
    commitCount: number, mode, reviewedAtUtc: '2026-11-02T12:00:00Z', unavailableReason: null,
    document: { verdict, ruleSetHash: 'sha256:' + 'e'.repeat(64), findings: number === 2 ? [{
      fingerprint: 'sha256:' + 'f'.repeat(64), ruleId: 'QS-NG-011', severity: 'high',
      path: 'frontend/src/app/example.ts', line: 42, message: 'Subscription needs teardown.',
    }] : [] },
  };
}

function view(state: string, landedEquals = true) {
  if (state === 'not-registered') return { state, repositoryId: null, currentHead: null,
    rounds: [], landedSha: null, landedBaseSha: null, landedPatchEqualsReviewedPatch: null };
  return {
    state, repositoryId: 'proj-002', currentHead: HEAD_TWO,
    rounds: [round(1, 'pass'), round(2,
      state === 'unavailable' ? 'timeout' : state === 'shadow' ? 'pass' : state,
      state === 'shadow' ? 'shadow' : 'decide')],
    landedSha: LANDED, landedBaseSha: BASE, landedPatchEqualsReviewedPatch: landedEquals,
  };
}

async function openEvidence(page: Page, id: string, watchPath: string): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);
  await page.getByTestId('prompt-tab-evidence').click();
  await expect(page.getByTestId('quality-panel')).toBeVisible();
}

test('Quality panel shows stored range states in both themes and never requests a review', async ({ page }) => {
  test.setTimeout(180_000);
  const watchPaths = await api<WatchPath[]>('/api/watch-paths');
  if (!watchPaths.length) throw new Error('No watch path available');
  const watchPath = watchPaths[0].path;
  const created = await createJob({ id: `e2e-quality-${Date.now()}`, title: 'Quality panel fixture',
    watchPath, targetState: '2-ready', requiresIntegration: false });
  await getJob(created.id, watchPath);
  await moveJob(created.id, watchPath, '5-human-review');
  let current = 'pass';
  let landedEquals = true;
  let reviewPosts = 0;
  let opened: URL | null = null;
  await page.route(/\/api\/(?:v1\/)?repos\/[^/]+\/change-review(?:\?|$)/, route => {
    reviewPosts++;
    return route.fulfill({ status: 500, body: 'Unexpected review request' });
  });
  await page.route('**/api/tasks/*/quality-studio**', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify(view(current, landedEquals)),
  }));
  await page.context().route('**/api/integrations/quality-studio/open**', route => {
    opened = new URL(route.request().url());
    return route.fulfill({ status: 200, contentType: 'text/html', body: '<!doctype html><title>Opened</title>' });
  });
  fs.mkdirSync(shots, { recursive: true });

  try {
    for (const theme of ['light', 'dark'] as const) {
      for (const state of ['pass', 'concerns', 'block', 'unavailable', 'shadow', 'not-registered']) {
        current = state;
        await openEvidence(page, created.id, watchPath);
        await setTheme(page, theme);
        await expect(page.getByTestId('quality-state')).toHaveAttribute('data-state', state);
        if (state !== 'not-registered') {
          await expect(page.getByTestId('quality-range')).toContainText('Round 2');
          await expect(page.getByTestId('quality-range')).toContainText(`${BASE.slice(0, 7)}..${HEAD_TWO.slice(0, 7)}`);
          await expect(page.getByTestId('quality-round')).toHaveValue('review-2');
          await page.getByTestId('quality-round').selectOption('review-1');
          await expect(page.getByTestId('quality-range')).toContainText(HEAD_ONE.slice(0, 7));
          await page.getByTestId('quality-round').selectOption('review-2');
        }
        await page.screenshot({ path: path.join(shots, `quality-${state}-${theme}--mocked.png`) });
      }
    }
    current = 'pass';
    await openEvidence(page, created.id, watchPath);
    const popup = page.context().waitForEvent('page');
    await page.getByTestId('quality-open-range').click();
    await popup;
    expect(opened).not.toBeNull();
    const target = new URL(opened!.searchParams.get('return')!, 'https://quality.test');
    expect(target.searchParams.get('view')).toBe('change');
    expect(target.searchParams.get('base')).toBe(BASE);
    expect(target.searchParams.get('head')).toBe(HEAD_TWO);
    landedEquals = false;
    await openEvidence(page, created.id, watchPath);
    await expect(page.getByTestId('quality-landed')).toContainText('landed patch differs');
    const landedPopup = page.context().waitForEvent('page');
    await page.getByTestId('quality-open-landed').click();
    await landedPopup;
    const landedTarget = new URL(opened!.searchParams.get('return')!, 'https://quality.test');
    expect(landedTarget.searchParams.get('base')).toBe(BASE);
    expect(landedTarget.searchParams.get('head')).toBe(LANDED);
    expect(reviewPosts).toBe(0);
  } finally {
    await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(watchPath)}`,
      { method: 'DELETE' }).catch(() => undefined);
  }
});

test('board shows the Quality Block dot only in review lanes', async ({ page }) => {
  test.setTimeout(90_000);
  const watchPaths = await api<WatchPath[]>('/api/watch-paths');
  if (!watchPaths.length) throw new Error('No watch path available');
  const watchPath = watchPaths[0].path;
  const title = `Quality board fixture ${Date.now()}`;
  const created = await createJob({ id: `e2e-quality-board-${Date.now()}`, title,
    watchPath, targetState: '2-ready', requiresIntegration: false, fixture: false });
  await getJob(created.id, watchPath);
  await moveJob(created.id, watchPath, '5-human-review');
  let verdict = 'block';
  let state = '5-human-review';
  const patchTasks = (value: unknown): unknown => {
    if (Array.isArray(value)) return value.map(patchTasks);
    if (value && typeof value === 'object') {
      const item = value as Record<string, unknown>;
      if (item.id === created.id) return { ...item, state, qualityStudioVerdict: verdict };
      return Object.fromEntries(Object.entries(item).map(([key, child]) => [key, patchTasks(child)]));
    }
    return value;
  };
  await page.route(/\/api\/(?:v1\/studio\/board|tasks\/grouped)(\?|$)/, async route => {
    const response = await route.fetch();
    return route.fulfill({ response, body: JSON.stringify(patchTasks(await response.json())) });
  });
  try {
    for (const theme of ['light', 'dark'] as const) {
      for (const sample of [
        { verdict: 'block', state: '4-review', dot: true },
        { verdict: 'block', state: '5-human-review', dot: true },
        { verdict: 'pass', state: '5-human-review', dot: false },
        { verdict: 'concerns', state: '5-human-review', dot: false },
        { verdict: 'block', state: '6-completed', dot: false },
      ]) {
        verdict = sample.verdict;
        state = sample.state;
        await page.goto('/');
        await setTheme(page, theme);
        const card = page.getByTestId('task-card').filter({ hasText: title }).first();
        await expect(card).toBeVisible();
        if (sample.dot) await expect(card.getByTestId('task-card-quality-block')).toBeVisible();
        else await expect(card.getByTestId('task-card-quality-block')).toHaveCount(0);
      }
    }
  } finally {
    await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(watchPath)}`,
      { method: 'DELETE' }).catch(() => undefined);
  }
});
