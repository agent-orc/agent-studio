import { test, expect, type Page, type TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { createJob, getJobDetail } from '../helpers/jobs';

const EVIDENCE_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.join(process.env.JOB_RESULTS_DIR, 'task-detail-simple-chat')
  : path.resolve('test-results', 'task-detail-simple-chat');

interface WatchPath { path: string }

let watchPath = '';
const createdTaskIds: string[] = [];

async function deleteTask(id: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
  }).catch(() => undefined);
}

async function createTask(title: string): Promise<string> {
  const task = await createJob({
    title,
    watchPath,
    cliType: 'claude',
    agent: 'claude',
    model: 'claude-opus-4-7',
    promptMarkdown: '# Simple task chat\n\nKeep the Activity conversation focused on messages.',
    targetState: '2-ready',
  });
  createdTaskIds.push(task.id);
  return task.id;
}

function taskDetailUrl(id: string): string {
  return `/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`;
}

async function openActivity(page: Page, id: string): Promise<void> {
  await page.goto(taskDetailUrl(id));
  const activityTab = page.getByTestId('inspector-tab-activity');
  await expect(activityTab).toBeVisible({ timeout: 15_000 });
  await activityTab.click();
  await expect(page.getByTestId('activity-chat-compose')).toBeVisible();
}

async function expectSimpleComposer(page: Page): Promise<void> {
  const composer = page.getByTestId('activity-chat-compose');
  await expect(composer.getByTestId('activity-chat-input')).toBeVisible();
  await expect(composer.getByTestId('activity-chat-send')).toBeVisible();
  await expect(composer.getByRole('button')).toHaveCount(1);

  for (const testId of [
    'activity-chat-mode',
    'activity-chat-mode-continue',
    'activity-chat-mode-steer',
    'activity-chat-mode-extend',
    'activity-chat-mode-newTask',
    'chat-compose-permission',
    'chat-compose-model',
    'chat-compose-context',
    'activity-chat-stop',
  ]) {
    await expect(composer.getByTestId(testId)).toHaveCount(0);
  }
  await expect(composer.getByText('Mode', { exact: true })).toHaveCount(0);
  for (const name of ['Continue', 'Steer', 'Extend', 'New task']) {
    await expect(composer.getByRole('button', { name, exact: true })).toHaveCount(0);
  }
}

async function setTheme(page: Page, theme: 'light' | 'dark'): Promise<void> {
  await page.evaluate((value) => {
    document.documentElement.dataset['studioTheme'] = value;
    localStorage.setItem('atp.studio.theme', value);
  }, theme);
}

async function captureComposer(
  page: Page,
  testInfo: TestInfo,
  name: string,
): Promise<void> {
  mkdirSync(EVIDENCE_DIR, { recursive: true });
  // Keep toolbar tooltips out of review evidence after responsive layout
  // shifts move the last click target underneath the stationary pointer.
  await page.getByTestId('activity-chat-input').hover({ position: { x: 4, y: 4 } });
  const screenshot = await page.getByTestId('pane-protocol').screenshot({
    path: path.join(EVIDENCE_DIR, `${name}.png`),
  });
  await testInfo.attach(`${name}.png`, { body: screenshot, contentType: 'image/png' });
}

test.describe('Task detail Activity chat is message-only', () => {
  test.beforeAll(async () => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    if (!paths.length) throw new Error('No watch path configured for task-detail chat coverage.');
    watchPath = paths[0].path;
  });

  test.afterEach(async () => {
    const ids = createdTaskIds.splice(0);
    await Promise.all(ids.map((id) => deleteTask(id)));
  });

  test('idle composer has one action, disabled and ready states, both themes', async ({ page }, testInfo) => {
    const id = await createTask(`simple-chat-idle-${Date.now()}`);
    await openActivity(page, id);
    await expectSimpleComposer(page);

    const input = page.getByTestId('activity-chat-input');
    const send = page.getByTestId('activity-chat-send');
    await expect(send).toBeDisabled();
    await input.fill('A clear follow-up');
    await expect(send).toBeEnabled();
    const [sendBackground, composerBackground] = await Promise.all([
      send.evaluate((element) => getComputedStyle(element).backgroundColor),
      page.getByTestId('activity-chat-compose')
        .evaluate((element) => getComputedStyle(element).backgroundColor),
    ]);
    expect(sendBackground).not.toBe('transparent');
    expect(sendBackground).not.toBe(composerBackground);

    await setTheme(page, 'light');
    await captureComposer(page, testInfo, 'task-detail-chat-light');
    await setTheme(page, 'dark');
    await captureComposer(page, testInfo, 'task-detail-chat-dark');
  });

  test('Ctrl+Enter and Cmd+Enter send default Continue without configuration overrides', async ({ page }) => {
    const id = await createTask(`simple-chat-keyboard-${Date.now()}`);
    const bodies: Record<string, unknown>[] = [];
    await page.route(`**/api/v1/projects/*/tasks/${encodeURIComponent(id)}/continue?**`, async (route) => {
      bodies.push(route.request().postDataJSON() as Record<string, unknown>);
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          status: 'queued',
          queued: { reason: 'project-busy', activeJobId: 'OTHER-1', position: 1 },
        }),
      });
    });

    await openActivity(page, id);
    const input = page.getByTestId('activity-chat-input');

    await input.fill('Sent with Control');
    await input.press('Control+Enter');
    await expect.poll(() => bodies.length).toBe(1);

    await input.fill('Sent with Command');
    await input.press('Meta+Enter');
    await expect.poll(() => bodies.length).toBe(2);

    for (const [index, body] of bodies.entries()) {
      expect(body).toEqual({
        prompt: index === 0 ? 'Sent with Control' : 'Sent with Command',
        mode: 'continue',
      });
      expect(body).not.toHaveProperty('model');
      expect(body).not.toHaveProperty('cliType');
      expect(body).not.toHaveProperty('thinkingLevel');
      expect(body).not.toHaveProperty('permissionMode');
    }
  });

  test('running task pauses and then sends through the same single action', async ({ page }) => {
    const id = await createTask(`simple-chat-running-${Date.now()}`);
    const detail = await getJobDetail(id, watchPath) as Record<string, unknown>;
    const info = detail['info'] as Record<string, unknown>;
    info['state'] = '3-progress';
    info['execution'] = {
      jobId: id,
      jobKey: `${watchPath}::${id}`,
      processId: 2188,
      startedAt: new Date(Date.now() - 10_000).toISOString(),
      status: 'running',
      exitCode: null,
      durationSeconds: null,
      model: 'claude-opus-4-7',
    };

    const escapedId = id.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const operations: string[] = [];
    let releaseStop: (() => void) | undefined;
    await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${escapedId}(?:\\?.*)?$`), (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }));
    await page.route(`**/api/v1/projects/*/tasks/${encodeURIComponent(id)}/stop?**`, async (route) => {
      operations.push(`stop:${new URL(route.request().url()).searchParams.get('reason')}`);
      await new Promise<void>((resolve) => { releaseStop = resolve; });
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });
    await page.route(`**/api/v1/projects/*/tasks/${encodeURIComponent(id)}/continue?**`, async (route) => {
      operations.push(`continue:${route.request().postDataJSON().mode}`);
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          status: 'started',
          execution: { ...info['execution'] as object, startedAt: new Date().toISOString() },
        }),
      });
    });

    await openActivity(page, id);
    await expectSimpleComposer(page);
    const input = page.getByTestId('activity-chat-input');
    const send = page.getByTestId('activity-chat-send');
    await expect(send).toHaveText('Send');
    await expect(send).toHaveAttribute('title', /Pause the current run/i);

    await input.fill('Apply this safely');
    await send.click();
    await expect.poll(() => operations).toEqual(['stop:followup']);
    await expect(send).toBeDisabled();
    await expect(send).toHaveText('Sending…');

    releaseStop?.();
    await expect.poll(() => operations).toEqual(['stop:followup', 'continue:continue']);
  });

  test('queued response is understandable without another action strip', async ({ page }) => {
    const id = await createTask(`simple-chat-queued-${Date.now()}`);
    await page.route(`**/api/v1/projects/*/tasks/${encodeURIComponent(id)}/continue?**`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          status: 'queued',
          queued: { reason: 'project-busy', activeJobId: 'OTHER-1', position: 1 },
        }),
      }));

    await openActivity(page, id);
    await page.getByTestId('activity-chat-input').fill('Queue this message');
    await page.getByTestId('activity-chat-send').click();

    await expect(page.getByTestId('activity-chat-queued')).toContainText('Message queued');
    await expectSimpleComposer(page);
  });

  test('failed response restores the draft and explains retry', async ({ page }) => {
    const id = await createTask(`simple-chat-error-${Date.now()}`);
    await page.route(`**/api/v1/projects/*/tasks/${encodeURIComponent(id)}/continue?**`, (route) =>
      route.fulfill({
        status: 503,
        contentType: 'application/json',
        body: JSON.stringify({ error: 'The runner is temporarily unavailable.' }),
      }));

    await openActivity(page, id);
    const input = page.getByTestId('activity-chat-input');
    await input.fill('Keep this draft for retry');
    await page.getByTestId('activity-chat-send').click();

    await expect(page.getByTestId('activity-chat-error')).toContainText('Send again to retry');
    await expect(input).toHaveValue('Keep this draft for retry');
    await expect(page.getByTestId('activity-chat-send')).toBeEnabled();
    await page.getByTestId('error-dialog-close').click();
  });

  test('compact width keeps the transcript composer and Send action usable', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 720, height: 900 });
    const id = await createTask(`simple-chat-compact-${Date.now()}`);
    await openActivity(page, id);
    await expectSimpleComposer(page);

    const composerBox = await page.getByTestId('activity-chat-compose').boundingBox();
    const inputBox = await page.getByTestId('activity-chat-input').boundingBox();
    const sendBox = await page.getByTestId('activity-chat-send').boundingBox();
    expect(composerBox).not.toBeNull();
    expect(inputBox).not.toBeNull();
    expect(sendBox).not.toBeNull();
    expect(inputBox!.width).toBeGreaterThan(160);
    expect(sendBox!.x + sendBox!.width).toBeLessThanOrEqual(composerBox!.x + composerBox!.width + 1);

    await captureComposer(page, testInfo, 'task-detail-chat-compact');
  });
});
