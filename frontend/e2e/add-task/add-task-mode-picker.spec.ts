import { test, expect } from '@playwright/test';

/**
 * Add Task dialog - Mode selector + "Allow web access" toggle.
 *
 * The create dialog exposes a Mode picker (Coding | Planning | Research) and a
 * single web-access toggle. Per Decision 2 of the task-modes design the toggle
 * has a per-mode default (research = on, everything else = off) but stays user
 * overridable. The chosen values are sent as CreateTaskRequest.mode /
 * CreateTaskRequest.allowWebAccess (backend tests cover the job.json write).
 *
 * The create POST is intercepted so no real task is written to the backend.
 */

interface WatchPath { path: string; name: string }

test.describe('Add Task - mode picker', () => {
  test('selects mode, defaults web access per mode, sends both on create', async ({ page }) => {
    const wps = await (await page.request.get('/api/watch-paths')).json() as WatchPath[];
    expect(wps.length).toBeGreaterThan(0);
    const target = wps[0];

    let createdBody: Record<string, unknown> | null = null;
    await page.route('**/api/tasks', async (route) => {
      if (route.request().method() !== 'POST') return route.fallback();
      createdBody = route.request().postDataJSON();
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ id: 'e2e-created-mode-task' }),
      });
    });

    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();
    await page.getByTestId('create-project-select').selectOption({ value: target.path });

    const picker = page.getByTestId('create-mode');
    await expect(picker).toBeVisible();

    const coding = page.getByTestId('create-mode-coding');
    const planning = page.getByTestId('create-mode-planning');
    const research = page.getByTestId('create-mode-research');
    const webToggle = page.getByTestId('create-web-access');

    // Default: coding active, web access off.
    await expect(coding).toHaveAttribute('aria-pressed', 'true');
    await expect(webToggle).toHaveAttribute('aria-checked', 'false');

    const codingPath = test.info().outputPath('mode-picker-coding-default.png');
    await dialog.screenshot({ path: codingPath });
    await test.info().attach('mode-picker-coding-default', { path: codingPath, contentType: 'image/png' });

    // Research turns web access on by default.
    await research.click();
    await expect(research).toHaveAttribute('aria-pressed', 'true');
    await expect(webToggle).toHaveAttribute('aria-checked', 'true');

    const researchPath = test.info().outputPath('mode-picker-research-web-on.png');
    await dialog.screenshot({ path: researchPath });
    await test.info().attach('mode-picker-research-web-on', { path: researchPath, contentType: 'image/png' });

    // Planning is read-only with web access off.
    await planning.click();
    await expect(planning).toHaveAttribute('aria-pressed', 'true');
    await expect(webToggle).toHaveAttribute('aria-checked', 'false');

    // The toggle is an independent control: opt web back on for the planning run.
    await webToggle.click();
    await expect(webToggle).toHaveAttribute('aria-checked', 'true');

    await page.getByTestId('create-title').fill('E2E mode-picker task');
    await page.getByTestId('create-submit').click();

    await expect.poll(() => createdBody?.['mode']).toBe('planning');
    expect(createdBody?.['allowWebAccess']).toBe(true);
  });
});
