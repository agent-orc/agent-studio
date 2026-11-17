import { test, expect, type Locator, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

// AGT-3867, F10. Runs only with PW_E2E_PROFILE=owner-day: the real backend
// seeds DEMO-19, an integrated card whose requirement-fit review returned
// concerns on the current brief. No route is mocked.
const RESULTS = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'owner-day-demo-19-acceptance-refusal');
const VARIANTS: readonly { theme: Theme; width: number }[] = [
  { theme: 'light', width: 1280 },
  { theme: 'light', width: 390 },
  { theme: 'dark', width: 390 },
  { theme: 'dark', width: 1280 },
];

async function openDemo19(page: Page, theme: Theme): Promise<void> {
  await page.addInitScript((value) => localStorage.setItem('atp.studio.theme', value), theme);
  await page.goto('/#/tasks/DEMO-19');
  await dismissDevErrorDialog(page);
  await setTheme(page, theme);
  await expect(page.getByTestId('task-detail-load-error')).toHaveCount(0);
}

async function shot(page: Page, name: string): Promise<void> {
  mkdirSync(RESULTS, { recursive: true });
  await page.screenshot({ path: join(RESULTS, `${name}.png`) });
}

test.describe('DEMO-19 acceptance refusal in the card detail (AGT-3867)', () => {
  test.describe.configure({ mode: 'serial', retries: 0 });
  test.skip(() => process.env.PW_E2E_PROFILE !== 'owner-day', 'Requires the isolated owner-day profile.');

  for (const { theme, width } of VARIANTS) {
    test(`offers the board's next steps and names the server reason (${theme} ${width})`, async ({ page }) => {
      test.setTimeout(120_000);
      await page.setViewportSize({ width, height: width < 600 ? 844 : 900 });
      await openDemo19(page, theme);
      const tag = `${theme}-${width}`;

      // The detail offers what the board card offers, for a concern on the current brief.
      const steps = page.getByTestId('acceptance-next-steps');
      await expect(steps).toHaveAttribute('data-readiness', 'review-concerns', { timeout: 30_000 });
      await expect(page.getByTestId('acceptance-next-steps-reason')).toContainText('Accept will be refused.');
      await expect(page.getByTestId('stale-brief-acceptance')).toHaveCount(0);
      await expect(steps.getByTestId('acceptance-next-steps-send-round')).toBeVisible();
      await expect(steps.getByTestId('review-current-brief')).toBeVisible();
      await expect(steps.getByTestId('accept-with-reason-open')).toBeVisible();
      await steps.scrollIntoViewIfNeeded();
      await shot(page, `demo-19-next-steps-${tag}`);

      if (width >= 1024) {
        // AGT-3830: no plain Accept to be refused. The refusal's next step
        // leads in the toolbar, with the reason beside it; a refused click
        // is covered by the AGT-3830 tests below.
        const hold = page.getByTestId('studio-triage-panel').getByTestId('acceptance-refusal');
        await expect(hold).toHaveAttribute('data-refusal-source', 'readiness', { timeout: 15_000 });
        await expect(hold).toHaveAttribute('data-replaces-accept', 'true');
        await expect(hold).toContainText('requirement-fit');
        await expect(page.getByTestId('studio-triage-action-mark-done')).toHaveCount(0);
        await hold.getByTestId('acceptance-refusal-action').hover();
        await expect(page.getByTestId('cac-tooltip')).toContainText('content review again');
        await shot(page, `demo-19-toolbar-next-step-${tag}`);
        await page.mouse.move(0, 0);
      }

      // The round opens prefilled from the detail; it is not sent here.
      await steps.getByTestId('acceptance-next-steps-send-round').click();
      const dialog = page.getByTestId('acceptance-round-dialog');
      await expect(dialog).toBeVisible();
      await expect(page.getByTestId('acceptance-round-prompt')).toHaveValue(/Close the acceptance gap on this card/);
      await shot(page, `demo-19-send-round-dialog-${tag}`);
      await page.getByTestId('acceptance-round-cancel').click();
      await expect(dialog).toHaveCount(0);
    });
  }
});

// AGT-3830, F3: DEMO-19's completion is refused on content grounds (review
// concerns: result SHA missing). The plain Accept is not the primary, and a
// refusal that still arrives keeps the card open with focus on the reason.
const HEADERS = { 'X-Client-Id': 'local-default' };
const shots = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'owner-day-refused-acceptance');

interface ReadinessItem { jobId: string; key: string | null; readiness: string; reason: string }

async function demo19Readiness(page: Page): Promise<{ item: ReadinessItem; watchPath: string }> {
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

async function openById(page: Page, card: { item: ReadinessItem; watchPath: string }, width: number, theme: Theme) {
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
    const card = await demo19Readiness(page);

    // 1. Readiness says review concerns: the next step is the primary, the reason sits next to it.
    await openById(page, card, view.width, view.theme);
    const shown = refusal(page, view.width);
    await expect(shown).toBeVisible({ timeout: 15_000 });
    await expect(shown).toHaveAttribute('data-replaces-accept', 'true');
    await expect(shown.getByTestId('acceptance-refusal-action')).toHaveText('Review again');
    await expect(shown.getByTestId('acceptance-refusal-override')).toHaveText('Accept with a reason…');
    await expect(shown.getByTestId('acceptance-refusal-reason')).toContainText('result SHA');
    await expect(plainAccept(page, view.width)).toHaveCount(0);
    await page.screenshot({ path: join(shots, `demo19-${view.name}-next-step.png`) });

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
    await openById(page, card, view.width, view.theme);
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
    await page.screenshot({ path: join(shots, `demo19-${view.name}-refused-focus.png`) });

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
      await page.screenshot({ path: join(shots, `demo19-${view.name}-notice.png`) });
    }
    await page.keyboard.press('Escape');
    await expect(notice).toHaveCount(0);
    await expect(page.getByTestId('studio-task').locator('app-job-detail')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'No task selected' })).toBeHidden();
  });
}
