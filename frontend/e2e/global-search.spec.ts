import { test, expect } from '@playwright/test';
import { installFrontendOverride } from './helpers/frontend-override';

/**
 * Drives the palette against a scripted event stream. Repository timing is not
 * reproducible from a checkout, so the proof we need - tasks land before
 * repository results and the operator sees per-domain progress - is scripted at
 * the EventSource seam, the same way the previous spec stubbed /api/search.
 *
 * Run it against the operator's Studio origin with `PW_FRONTEND_OVERRIDE`
 * pointing at the worktree's `ng serve` to verify a change before it ships.
 */
const scriptStream = `
  class ScriptedEventSource extends EventTarget {
    constructor() {
      super();
      const frame = (name, payload, delay) => setTimeout(() => this.dispatchEvent(
        new MessageEvent(name, { data: JSON.stringify(payload) })), delay);
      frame('meta', { query: 'README', repositories: 2 }, 0);
      frame('tasks', { durationMs: 6, error: null, items: [{
        domain: 'tasks', projectName: 'Agent Studio', projectColor: '#569cd6',
        title: 'Palette streams per domain', subtitle: 'AGT-2723', taskKey: 'k1', lane: '3-progress',
      }] }, 0);
      // A slow first repository: long enough for the assertions below to observe
      // the progress state and the two-second patience note before it answers.
      frame('repository', {
        name: 'alpha', index: 1, total: 2, durationMs: 4000, commitsCache: 'miss', filesCache: 'miss', commitsError: null, filesError: null,
        commits: [], files: [{
          domain: 'files', projectName: 'Agent Studio', projectColor: '#569cd6',
          title: 'README.md', subtitle: 'README.md', path: 'README.md', isWiki: false,
        }],
      }, 4000);
      frame('repository', {
        name: 'beta', index: 2, total: 2, durationMs: 40, commitsCache: 'hit', filesCache: 'hit', commitsError: null, filesError: null,
        commits: [], files: [{
          domain: 'files', projectName: 'Agent Studio', projectColor: '#569cd6',
          title: 'README-beta.md', subtitle: 'docs/README-beta.md', path: 'docs/README-beta.md', isWiki: true,
        }],
      }, 4600);
      frame('done', { durationMs: 4650, errors: {} }, 4800);
    }
    close() {}
  }
  window.EventSource = ScriptedEventSource;
`;

test('global palette streams tasks before repository results and shows per-domain progress', async ({ page }) => {
  await installFrontendOverride(page);
  // A worktree checkout always has uncommitted work, so the crash-recovery
  // review modal would cover the palette. It is unrelated to this feature.
  await page.route('**/api/crash-recovery/pending', route => route.fulfill({
    contentType: 'application/json', body: JSON.stringify({ pending: [] }),
  }));
  // Seed the theme without overwriting it on later reloads: the second half of
  // this spec switches to dark and reloads, and an unconditional set here would
  // put light straight back.
  await page.addInitScript(() =>
    localStorage.setItem('atp.studio.theme', localStorage.getItem('atp.studio.theme') ?? 'light'));
  await page.addInitScript({ content: scriptStream });
  await page.goto('/');
  // A startup runner-status error can raise an unrelated global dialog. Clear
  // it before the palette opens so it stays out of the evidence frame.
  await page.getByTestId('error-dialog-close').click({ timeout: 2_000 }).catch(() => {});

  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  const input = page.getByTestId('global-search-input');
  await expect(input).toBeFocused();
  await input.fill('README');

  // Tasks answer first; the repository domains still report progress.
  await expect(page.getByTestId('global-search-group-tasks')).toContainText('Palette streams per domain');
  await expect(page.getByTestId('global-search-status-files')).toContainText('of 2 repositories');
  await expect(page.getByTestId('global-search-group-files')).not.toContainText('README.md');
  await expect(page.getByTestId('global-search-elapsed')).toBeVisible();
  await expect(page.getByTestId('global-search-patience')).toBeVisible();
  const progressShot = process.env.GLOBAL_SEARCH_PROGRESS_SCREENSHOT;
  if (progressShot) await page.screenshot({ path: progressShot, fullPage: true });

  // Repositories fill in as they finish, in arrival order.
  await expect(page.getByTestId('global-search-group-files')).toContainText('README.md');
  await expect(page.getByTestId('global-search-status-files')).toContainText('2 results');

  await page.keyboard.press('ArrowDown');
  await expect(page.locator('[role="option"][aria-selected="true"]')).toHaveCount(1);

  const screenshotPath = process.env.GLOBAL_SEARCH_SCREENSHOT;
  if (screenshotPath) await page.screenshot({ path: screenshotPath, fullPage: true });

  await page.keyboard.press('Escape');
  await page.evaluate(() => localStorage.setItem('atp.studio.theme', 'dark'));
  await page.reload();
  await page.getByTestId('error-dialog-close').click({ timeout: 2_000 }).catch(() => {});
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  await expect(page.getByTestId('global-search-input')).toBeVisible();
  await page.getByTestId('global-search-input').fill('README');
  await expect(page.getByTestId('global-search-status-files')).toContainText('of 2 repositories');
  const darkShot = process.env.GLOBAL_SEARCH_DARK_SCREENSHOT;
  if (darkShot) await page.screenshot({ path: darkShot, fullPage: true });
});
