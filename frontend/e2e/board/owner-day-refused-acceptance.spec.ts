import { test, expect, type Locator, type Page } from '@playwright/test';
import { mkdirSync } from 'fs';
import * as path from 'path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

// AGT-3830: DEMO-19's completion is refused on content grounds (review
// concerns: result SHA missing). Runs only with PW_E2E_PROFILE=owner-day,
// against the real backend and the seeded owner-day workspace.
const HEADERS = { 'X-Client-Id': 'local-default' };
const shots = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve(__dirname, '../../test-results/owner-day-refused-acceptance');

interface ReadinessItem { jobId: string; key: string | null; readiness: string; reason: string }

async function demo19(page: Page): Promise<{ item: ReadinessItem; watchPath: string }> {
  const read = () => page.request.post('/api/tasks/acceptance-readiness', { data: { project: 'Demo App' }, headers: HEADERS });
  // Wait for the seeded workspace scan; other owner-day specs may accept the ready cards first.
  await expect.poll(async () => {
    const response = await read();
    return response.ok() ? (await response.json()).items.some((c: ReadinessItem) => c.key === 'DEMO-19') : false;
  }, { timeout: 30_000 }).toBe(true);
  const item = (await (await read()).json()).items.find((c: ReadinessItem) => c.key === 'DEMO-19') as ReadinessItem;
  expect(item).toMatchObject({ readiness: 'review-concerns' });
  const watchPaths = await (await page.request.get('/api/watch-paths?includeFixtures=true', { headers: HEADERS })).json();
  return { item, watchPath: watchPaths.find((w: { name?: string }) => w.name === 'Demo App').path };
}

async function open(page: Page, card: { item: ReadinessItem; watchPath: string }, width: number, theme: 'light' | 'dark') {
  await page.setViewportSize({ width, height: width < 600 ? 844 : 800 });
  await page.goto(`/?job=${encodeURIComponent(card.item.jobId)}&watchPath=${encodeURIComponent(card.watchPath)}`);
  await dismissDevErrorDialog(page);
  await setTheme(page, theme);
  await expect(page.getByTestId('studio-task').locator('app-job-detail')).toBeVisible({ timeout: 30_000 });
}

/** The refusal the viewport shows: the phone's full-width block or the toolbar item. */
function refusal(page: Page, width: number): Locator {
  return width < 600
    ? page.locator('app-mobile-lane-primary').getByTestId('acceptance-refusal')
    : page.getByTestId('studio-triage-panel').getByTestId('acceptance-refusal');
}

function plainAccept(page: Page, width: number): Locator {
  return page.getByTestId(width < 600 ? 'studio-mobile-triage-action-mark-done' : 'studio-triage-action-mark-done');
}

function overlaps(a: { x: number; y: number; width: number; height: number }, b: { x: number; y: number; width: number; height: number }) {
  return a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;
}

const VIEWS = [
  { name: 'phone-light', width: 390, theme: 'light' },
  { name: 'phone-dark', width: 390, theme: 'dark' },
  { name: 'desktop-light', width: 1280, theme: 'light' },
] as const;

for (const view of VIEWS) {
  test(`DEMO-19 leads with the refusal's next step and keeps the card on a refused Accept (${view.name})`, async ({ page }) => {
    test.skip(process.env.PW_E2E_PROFILE !== 'owner-day', 'Requires the isolated owner-day profile.');
    test.setTimeout(120_000);
    mkdirSync(shots, { recursive: true });
    const card = await demo19(page);

    // 1. Readiness says review concerns: the next step is the primary, the reason sits next to it.
    await open(page, card, view.width, view.theme);
    const shown = refusal(page, view.width);
    await expect(shown).toBeVisible({ timeout: 15_000 });
    await expect(shown).toHaveAttribute('data-replaces-accept', 'true');
    await expect(shown.getByTestId('acceptance-refusal-action')).toHaveText('Review again');
    await expect(shown.getByTestId('acceptance-refusal-override')).toHaveText('Accept with a reason…');
    await expect(shown.getByTestId('acceptance-refusal-reason')).toContainText('result SHA');
    await expect(plainAccept(page, view.width)).toHaveCount(0);
    await page.screenshot({ path: path.join(shots, `demo19-${view.name}-next-step.png`) });

    // 2. A refusal that still arrives (a stale "ready" answer let Accept through) keeps the card
    //    open, moves focus to the reason, and raises no notice over the header.
    let pretendReady = true;
    await page.route('**/api/tasks/acceptance-readiness', async route => {
      const response = await route.fetch();
      const body = await response.json();
      if (pretendReady) {
        for (const item of body.items) if (item.key === 'DEMO-19') item.readiness = 'ready';
      }
      await route.fulfill({ response, json: body });
    });
    await open(page, card, view.width, view.theme);
    const accept = plainAccept(page, view.width);
    await expect(accept).toBeVisible({ timeout: 15_000 });
    // Stop pretending only once the click sent the move: the detail's own
    // readiness re-checks (AGT-3867 next steps) would otherwise replace
    // Accept before the click lands.
    page.on('request', request => {
      if (new URL(request.url()).pathname.endsWith('/move') && request.method() === 'POST') pretendReady = false;
    });
    const move = page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/move') && r.request().method() === 'POST');
    await accept.click();
    expect((await move).status()).toBe(409);
    await expect(shown).toBeVisible({ timeout: 15_000 });
    await expect(shown.getByTestId('acceptance-refusal-focus')).toBeFocused();
    await expect(page.getByTestId('task-move-refusal')).toBeVisible();
    const refusalToast = page.getByTestId('notification-stack-below-toolbar').getByTestId('notification-warning');
    await expect(refusalToast).toBeVisible();
    await expect(refusalToast).toHaveAttribute('role', /status|alert/);
    await expect(page.getByRole('heading', { name: 'No task selected' })).toBeHidden();
    await expect(page.getByTestId('studio-task').locator('app-job-detail')).toBeVisible();
    await expect(plainAccept(page, view.width)).toHaveCount(0);
    await page.screenshot({ path: path.join(shots, `demo19-${view.name}-refused-focus.png`) });

    // 3. The refusal notice closes with Escape before the card does, and on a
    //    phone stays clear of the header controls.
    const notice = refusalToast;
    await expect(notice).toBeVisible();
    await expect(notice).toHaveAttribute('role', /status|alert/);
    if (view.width < 600) {
      const box = (await notice.boundingBox())!;
      for (const control of [page.getByTestId('studio-attention-trigger'), shown.getByTestId('acceptance-refusal-action')]) {
        if (!(await control.isVisible())) continue;
        expect(overlaps(box, (await control.boundingBox())!), 'notice covers a header control').toBe(false);
      }
      expect(overlaps(box, (await page.getByTestId('studio-statusbar').boundingBox())!),
        'notice is obscured by the wrapped status bar').toBe(false);
      await page.screenshot({ path: path.join(shots, `demo19-${view.name}-notice.png`) });
    }
    await page.keyboard.press('Escape');
    await expect(notice).toHaveCount(0);
    await expect(page.getByTestId('studio-task').locator('app-job-detail')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'No task selected' })).toBeHidden();
  });
}
