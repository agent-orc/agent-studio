import { expect, test, type Locator } from '@playwright/test';
import { join } from 'node:path';
import { installSyntheticBoard } from './large-board.fixture';
import { setTheme } from '../helpers/theme';

const results = process.env.JOB_RESULTS_DIR ?? 'test-results';
const scrollLaneToEnd = (lane: Locator) => lane.evaluate(element => {
  const group = element.closest<HTMLElement>('.lane-group__lanes');
  if (group) group.scrollTop += element.getBoundingClientRect().bottom - group.getBoundingClientRect().bottom;
  else element.scrollTop = element.scrollHeight;
});

test('a 41-card lane keeps its footer and header actions', async ({ page }) => {
  await installSyntheticBoard(page, 41, true);
  await page.goto('/');
  const lane = page.getByTestId('lane-scroll-2-ready');
  await expect(page.getByTestId('lane-count-2-ready')).toContainText('41');
  await expect(page.getByTestId('lane-collapse-2-ready')).toBeVisible();
  await expect(page.getByTestId('archive-all-btn')).toBeVisible();
  await scrollLaneToEnd(lane);
  await expect(lane.getByTestId('lane-drop-slot-end')).toHaveCount(1);
  await expect(lane.getByTestId('lane-add-task')).toBeVisible();
  await lane.getByTestId('lane-add-task').click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.getByTestId('create-dialog-header')).toHaveText('New Ready Task');
});

test('a generated 830-card lane retains totals, search, scrolling and offscreen reorder', async ({ page }) => {
  test.setTimeout(180_000);
  await installSyntheticBoard(page, 830, true);
  await page.goto('/');
  const lane = page.getByTestId('lane-scroll-2-ready');
  await expect(page.getByTestId('lane-count-2-ready')).toContainText('830', { timeout: 30_000 });
  await expect(lane.locator('app-job-card').first()).toBeVisible();
  expect(await lane.locator('app-job-card').count()).toBeLessThan(40);

  await lane.getByTestId('task-card').first().focus();
  await page.keyboard.press('PageDown');
  await expect.poll(() => lane.locator('[data-window-index] [data-testid="task-card"]:focus')
    .evaluate(element => Number(element.closest<HTMLElement>('[data-window-index]')?.dataset['windowIndex']))).toBeGreaterThan(1);
  await page.keyboard.press('End');
  await expect(lane.locator('app-job-card').filter({ hasText: 'Window target AGT-S00829' })).toBeVisible();
  await page.keyboard.press('Home');
  await expect(lane.locator('[data-window-index="0"] [data-testid="task-card"]')).toBeFocused();

  await scrollLaneToEnd(lane);
  await expect(lane.locator('app-job-card').filter({ hasText: 'Window target AGT-S00829' })).toBeVisible();
  expect(await lane.locator('app-job-card').count()).toBeLessThan(40);
  await expect(lane.getByTestId('lane-drop-slot-end')).toHaveCount(1);
  await expect(lane.getByTestId('lane-add-task')).toBeVisible();
  await expect(page.getByTestId('lane-collapse-2-ready')).toBeVisible();
  await expect(page.getByTestId('archive-all-btn')).toBeVisible();

  let reordered: string[] | null = null;
  await page.route('**/api/tasks/reorder', async route => {
    reordered = (route.request().postDataJSON() as { jobs: { jobId: string }[] }).jobs.map(job => job.jobId);
    await route.fulfill({ status: 200, json: {} });
  });
  await lane.evaluate(element => {
    const spacer = element.querySelector<HTMLElement>('[data-testid="lane-window-before"]');
    if (!spacer) throw new Error('No offscreen drop region');
    const transfer = new DataTransfer();
    transfer.setData('text/plain', JSON.stringify({
      jobId: 'AGT-S00000', watchPath: 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard',
      taskKey: 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard::AGT-S00000',
    }));
    transfer.setData('application/x-source-state', '2-ready');
    const rect = (element.closest('.lane-group__lanes') ?? element).getBoundingClientRect();
    spacer.dispatchEvent(new DragEvent('drop', {
      bubbles: true, cancelable: true, dataTransfer: transfer,
      clientX: rect.left + 20, clientY: rect.top + rect.height / 2,
    }));
  });
  await expect.poll(() => reordered?.length ?? 0).toBe(830);
  await expect.poll(() => reordered?.indexOf('AGT-S00000') ?? -1).toBeGreaterThan(400);

  await page.goto('/?q=Window%20target%20AGT-S00829');
  await expect(page.getByTestId('lane-count-2-ready')).toContainText('1');
  await expect(lane.locator('app-job-card')).toHaveCount(1);
  await page.goto('/');
  await expect(page.getByTestId('lane-count-2-ready')).toContainText('830');

  await page.goto('/?type=bug');
  await expect(page.getByTestId('lane-count-2-ready')).toContainText('1');
  await expect(lane.locator('app-job-card')).toHaveCount(1);
  await page.goto('/');
  await expect(page.getByTestId('lane-count-2-ready')).toContainText('830');

  const moveRequests: { targetState: string; targetIndex?: number }[] = [];
  await page.route('**/api/v1/projects/*/tasks/*/move*', async route => {
    moveRequests.push(route.request().postDataJSON());
    await new Promise(resolve => setTimeout(resolve, 2000));
    await route.fulfill({ status: 200, json: {} });
  });
  await lane.locator('app-job-card').filter({ hasText: 'Generated task AGT-S00000' }).click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Move to Accepted' }).click();
  await expect.poll(() => moveRequests.some(request => request.targetState === '6-completed')).toBe(true);
  await expect(page.getByTestId('lane-count-2-ready')).toContainText('829');

  await scrollLaneToEnd(lane);
  await lane.evaluate(element => {
    const spacer = element.querySelector<HTMLElement>('[data-testid="lane-window-before"]');
    if (!spacer) throw new Error('No offscreen cross-lane drop region');
    const transfer = new DataTransfer();
    transfer.setData('text/plain', JSON.stringify({
      jobId: 'AGT-X00000', watchPath: 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard',
      taskKey: 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard::AGT-X00000',
    }));
    transfer.setData('application/x-source-state', '6-completed');
    const rect = (element.closest('.lane-group__lanes') ?? element).getBoundingClientRect();
    spacer.dispatchEvent(new DragEvent('drop', {
      bubbles: true, cancelable: true, dataTransfer: transfer,
      clientX: rect.left + 20, clientY: rect.top + rect.height / 2,
    }));
  });
  await expect.poll(() => moveRequests.find(request => request.targetState === '2-ready')?.targetIndex ?? -1)
    .toBeGreaterThan(400);
  await expect(page.getByTestId('lane-count-2-ready')).toContainText('830');

  const group = lane.locator('xpath=ancestor::*[contains(@class,"lane-group__lanes")]');
  const savedScroll = await group.evaluate(element => element.scrollTop);
  expect(savedScroll).toBeGreaterThan(1000);
  await lane.locator('app-job-card').filter({ hasText: 'Window target AGT-S00829' }).click();
  await page.getByTestId('studio-tab-board:__all__').click();
  await expect.poll(() => group.evaluate(element => element.scrollTop)).toBeGreaterThan(savedScroll * 0.8);
  await expect(lane.locator('app-job-card').filter({ hasText: 'Window target AGT-S00829' })).toBeVisible();
});

test('a generated 3,000-card board mounts a bounded window and renders in both themes and widths', async ({ page }) => {
  test.setTimeout(180_000);
  await installSyntheticBoard(page, 3000, true);
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.goto('/');
  const lane = page.getByTestId('lane-scroll-2-ready');
  await expect(page.getByTestId('lane-count-2-ready')).toContainText('3000', { timeout: 30_000 });
  expect(await lane.locator('app-job-card').count()).toBeLessThan(40);
  await expect(page.getByTestId('lane-collapse-2-ready')).toBeVisible();
  await expect(page.getByTestId('archive-all-btn')).toBeVisible();
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 800 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: join(results, `large-board-${width}-${theme}--generated.png`), fullPage: false });
    }
  }
  await scrollLaneToEnd(lane);
  await expect(lane.locator('app-job-card').filter({ hasText: 'Window target AGT-S02999' })).toBeVisible();
  expect(await lane.locator('app-job-card').count()).toBeLessThan(40);
  await expect(lane.getByTestId('lane-drop-slot-end')).toHaveCount(1);
  await expect(lane.getByTestId('lane-add-task')).toBeVisible();
});
