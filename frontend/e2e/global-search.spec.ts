import type { Page } from '@playwright/test';
import { test, expect } from './fixtures/dev-backend';
import { dismissDevErrorDialog, setTheme } from './helpers/theme';

/**
 * Streams `/api/search/stream` from inside the page.
 *
 * Playwright's `route.fulfill` writes a whole body at once, which cannot
 * express "tasks landed while the repositories are still being searched" -
 * the property this palette exists to have. Stubbing `window.fetch` for this
 * one endpoint gives a real `ReadableStream` with real delays, so the spec
 * drives the production service parser and the production component reducer.
 *
 * @param repositoryDelayMs how long the repository sweep "takes".
 */
async function installSearchStream(page: Page, repositoryDelayMs: number) {
  await page.addInitScript((delay: number) => {
    const original = window.fetch.bind(window);
    window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
      if (!url.includes('/api/search/stream')) return original(input, init);

      const encoder = new TextEncoder();
      const frame = (event: string, data: unknown) =>
        encoder.encode(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);

      let timer: ReturnType<typeof setTimeout>;
      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(frame('start', { query: 'readme-proof', repositories: 2, domains: ['tasks', 'commits', 'files'] }));
          controller.enqueue(frame('chunk', {
            domain: 'tasks', projectName: '', durationMs: 6, cacheHit: true,
            items: [{
              domain: 'tasks', projectName: 'Agent Studio', projectColor: '#569cd6',
              title: 'Indexed README task', subtitle: 'AGT-2723', taskKey: 'indexed-readme-task', lane: '2-ready',
            }],
          }));
          timer = setTimeout(() => {
            controller.enqueue(frame('chunk', {
              domain: 'files', projectName: 'Agent Studio', durationMs: delay, cacheHit: false,
              items: [{
                domain: 'files', projectName: 'Agent Studio', projectColor: '#569cd6',
                title: 'README.md', subtitle: 'README.md', path: 'README.md', isWiki: false,
              }],
            }));
            controller.enqueue(frame('progress', { domain: 'files', completed: 2, total: 2 }));
            controller.enqueue(frame('progress', { domain: 'commits', completed: 2, total: 2 }));
            controller.enqueue(frame('done', { durationMs: delay, domainDurationMs: { tasks: 6 }, repositories: 2 }));
            controller.close();
          }, delay);

          init?.signal?.addEventListener('abort', () => {
            clearTimeout(timer);
            controller.error(new DOMException('Aborted', 'AbortError'));
          });
        },
      });

      return Promise.resolve(new Response(stream, {
        status: 200,
        headers: { 'Content-Type': 'text/event-stream' },
      }));
    };
  }, repositoryDelayMs);
}

/**
 * Clears the global dialogs an isolated worktree stack raises at startup: the
 * runner-status error dialog and the crash-recovery prompt. Both own the modal
 * stack, so an undismissed one swallows the palette's Escape and paints over
 * the evidence frame. "Leave all uncommitted" is the decline branch of the
 * recovery prompt, so this commits nothing.
 */
async function dismissGlobalDialogs(page: Page) {
  await dismissDevErrorDialog(page);
  await page.getByTestId('error-dialog-close').click({ timeout: 2_000 }).catch(() => {});
  await page.getByTestId('crash-recovery-dismiss-all').click({ timeout: 2_000 }).catch(() => {});
}

test('palette shows task results while repositories are still being searched', async ({ page }) => {
  await installSearchStream(page, 4000);
  await page.addInitScript(() => localStorage.setItem('atp.studio.theme', 'light'));
  await page.goto('/');
  await dismissGlobalDialogs(page);
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');

  const input = page.getByTestId('global-search-input');
  await expect(input).toBeFocused();
  await input.fill('readme-proof');

  // Tasks are on screen and settled while the repository sweep is still open.
  await expect(page.getByTestId('global-search-group-tasks')).toContainText('Indexed README task');
  await expect(page.getByTestId('global-search-status-tasks')).toHaveText('1 result');
  await expect(page.getByTestId('global-search-status-files')).toHaveText('0 of 2 repositories');
  await expect(page.getByTestId('global-search-group-files')).not.toContainText('README.md');
  await expect(page.getByTestId('global-search-elapsed')).toBeVisible();

  // A search that runs long explains itself instead of spinning silently.
  await expect(page.getByTestId('global-search-patience')).toBeVisible();

  // Evidence frame of the state this task exists to create: tasks answered,
  // repositories still counting up.
  const screenshotPath = process.env.GLOBAL_SEARCH_SCREENSHOT;
  if (screenshotPath) {
    await page.screenshot({ path: screenshotPath.replace(/\.png$/, '-searching.png'), fullPage: true });
  }

  // Repository results append; the task group above them does not move.
  await expect(page.getByTestId('global-search-group-files')).toContainText('README.md', { timeout: 10_000 });
  await expect(page.getByTestId('global-search-status-files')).toHaveText('1 result');
  await expect(page.getByTestId('global-search-elapsed')).toBeHidden();
  await expect(page.getByTestId('global-search-group-tasks')).toContainText('Indexed README task');

  await page.keyboard.press('ArrowDown');
  await expect(page.locator('[role="option"][aria-selected="true"]')).toHaveCount(1);

  if (screenshotPath) await page.screenshot({ path: screenshotPath, fullPage: true });

  await page.keyboard.press('Escape');
  await expect(page.getByTestId('global-search-input')).toBeHidden();

  // The same per-domain rows must read in the dark theme too.
  await page.reload();
  await dismissGlobalDialogs(page);
  await setTheme(page, 'dark');
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  await expect(page.getByTestId('global-search-input')).toBeVisible();
  await page.getByTestId('global-search-input').fill('readme-proof');
  await expect(page.getByTestId('global-search-status-files')).toHaveText('0 of 2 repositories');
  if (screenshotPath) {
    await page.screenshot({ path: screenshotPath.replace(/\.png$/, '-dark.png'), fullPage: true });
  }
});

test('escape cancels an in-flight repository search', async ({ page }) => {
  await installSearchStream(page, 30_000);
  await page.goto('/');
  await dismissGlobalDialogs(page);
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  await page.getByTestId('global-search-input').fill('readme-proof');
  await expect(page.getByTestId('global-search-status-files')).toHaveText('0 of 2 repositories');

  await page.keyboard.press('Escape');

  // The palette is gone and, when reopened, carries no state from the
  // cancelled search - the abort tore the request down rather than leaving it
  // to land later.
  await expect(page.getByTestId('global-search-input')).toBeHidden();
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  await expect(page.getByTestId('global-search-input')).toHaveValue('');
  await expect(page.getByTestId('global-search-group-files')).toBeHidden();
});
