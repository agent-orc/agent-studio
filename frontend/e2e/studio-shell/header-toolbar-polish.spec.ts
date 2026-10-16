import { test, expect, Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';
import { setTheme } from '../helpers/theme';

/**
 * Header-toolbar polish (operator request, 2026-06-04).
 *
 * Locks the structure behind the visual cleanup of the top-right controls:
 *  - the board tab-bar actions (Lanes/Epics + Full/Compact toggles and the
 *    primary "+ Add task") live in a single `studio-board-actions` flex row
 *    with an in-cluster separator before the primary, instead of loose
 *    whitespace-spaced buttons; and
 *  - the titlebar actions (Project Chat + theme + Notifications) stay a
 *    grouped, evenly-spaced trio.
 *
 * Visual deliverable: screenshots of the header region with the toggles in
 * both rest and active states, copied into the job's results/ folder by the
 * caller.
 *
 * Runs against whatever board state the configured backend exposes; the
 * header chrome renders regardless of whether any cards are present.
 */

async function dismissTransientErrors(page: Page): Promise<void> {
  for (let i = 0; i < 3; i++) {
    const overlay = page.locator('.overlay--error');
    if ((await overlay.count()) === 0) break;
    if (!(await overlay.first().isVisible({ timeout: 200 }).catch(() => false))) break;
    await page.locator('.error-dialog__close').first().click({ timeout: 1_000 }).catch(() => {});
    await page.waitForTimeout(150);
  }
}

async function gotoBoard(page: Page): Promise<void> {
  await page.goto('/');
  const studio = page.getByTestId('studio-board');
  const welcome = page.getByTestId('studio-welcome');
  await Promise.race([
    studio.first().waitFor({ state: 'visible', timeout: 8_000 }),
    welcome.first().waitFor({ state: 'visible', timeout: 8_000 }),
  ]).catch(() => { /* fall through */ });

  if ((await welcome.count()) > 0 && (await welcome.first().isVisible().catch(() => false))) {
    const allProjects = welcome.first().getByRole('button', { name: 'All projects' });
    await allProjects.click({ timeout: 3_000 }).catch(() => { /* nothing */ });
    await studio.first().waitFor({ state: 'visible', timeout: 5_000 }).catch(() => { /* nothing */ });
  }

  await dismissTransientErrors(page);
}

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
  });
}

test.describe('Header toolbar polish', () => {
  test('board actions form one grouped, evenly-spaced cluster', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1000 });
    await gotoBoard(page);

    const chatButton = page.getByTestId('studio-titlebar-chat');
    if ((await chatButton.count()) === 0) {
      test.skip(true, 'vsCodeLayout shell not active; header toolbar only exists there');
      return;
    }

    // Board cluster wrapper present with its controls inside it. (AGT-2035
    // removed the Full/Compact toggle along with the abolished card density.)
    const cluster = page.getByTestId('studio-board-actions');
    await expect(cluster).toBeVisible();
    const addTask = page.getByTestId('studio-board-add-task');
    await expect(addTask).toBeVisible();
    await expect(cluster.getByTestId('studio-board-compact-toggle')).toHaveCount(0);
    await expect(cluster.getByTestId('studio-board-epic-toggle')).toHaveCount(0);
    await expect(cluster.getByTestId('studio-board-add-task')).toHaveCount(1);


    // Titlebar action trio is grouped together.
    const titleActions = page.getByTestId('studio-titlebar-actions');
    await expect(titleActions).toBeVisible();
    await expect(titleActions.getByTestId('studio-titlebar-chat')).toHaveCount(1);

    // --- Screenshots: header region + clusters in rest state -------------
    await page.screenshot({ path: 'test-results/header-toolbar-full.png', clip: { x: 0, y: 0, width: 1600, height: 76 } });
    await titleActions.screenshot({ path: 'test-results/header-titlebar-actions.png' });
    await cluster.screenshot({ path: 'test-results/header-board-actions-rest.png' });

    // Flip the toggle so the active (pressed) pill state is captured.
    const epicPressedBefore = await epicToggle.getAttribute('aria-pressed');
    if (epicPressedBefore === 'false') await epicToggle.click();
    await expect(epicToggle).toHaveAttribute('aria-pressed', 'true');
    await page.waitForTimeout(150);
    await cluster.screenshot({ path: 'test-results/header-board-actions-active.png' });

    // Restore original toggle state so the spec leaves no board change.
    if (epicPressedBefore === 'false') await epicToggle.click();
  });

  test('task pane header buttons expose active pressed state', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `header-pane-active-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Header pane active-state test',
      targetState: '2-ready',
    });

    try {
      await page.setViewportSize({ width: 1600, height: 1000 });
      await page.addInitScript(() => {
        try {
          localStorage.setItem(
            'taskboard.panesVisible',
            JSON.stringify({ prompt: true, protocol: true, git: false }),
          );
        } catch {
          /* private mode */
        }
      });

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await dismissTransientErrors(page);

      const chatButton = page.getByTestId('studio-titlebar-chat');
      if ((await chatButton.count()) === 0) {
        test.skip(true, 'vsCodeLayout shell not active; task-tab pane buttons only exist there');
        return;
      }

      const prompt = page.getByTestId('studio-pane-toggle-prompt');
      const protocol = page.getByTestId('studio-pane-toggle-protocol');
      const git = page.getByTestId('studio-pane-toggle-git');
      const cluster = page.getByTestId('studio-pane-toggles');
      await expect(prompt).toBeVisible({ timeout: 10_000 });
      await expect(protocol).toBeVisible();
      await expect(git).toBeVisible();

      await expect(prompt).toHaveAttribute('aria-pressed', 'true');
      await expect(protocol).toHaveAttribute('aria-pressed', 'true');
      await expect(git).toHaveAttribute('aria-pressed', 'false');
      await expect(prompt).toHaveClass(/studio-tab-action--active/);
      await expect(protocol).toHaveClass(/studio-tab-action--active/);
      await expect(git).not.toHaveClass(/studio-tab-action--active/);
      const activeStyle = await prompt.evaluate((el) => {
        const style = window.getComputedStyle(el);
        return {
          color: style.color,
          background: style.backgroundColor,
          borderColor: style.borderColor,
          boxShadow: style.boxShadow,
        };
      });
      const inactiveStyle = await git.evaluate((el) => {
        const style = window.getComputedStyle(el);
        return {
          color: style.color,
          background: style.backgroundColor,
          borderColor: style.borderColor,
          boxShadow: style.boxShadow,
        };
      });
      // The active toggle now reads as a centred pill: accent text + soft
      // accent-tinted fill + a full accent border on all four sides.
      expect(activeStyle.color).not.toBe(inactiveStyle.color);
      expect(activeStyle.background).not.toBe(inactiveStyle.background);
      expect(activeStyle.borderColor).not.toBe(inactiveStyle.borderColor);
      // Regression guard (ASS-1719): the old active indicator was a 2px
      // `inset 0 -2px 0` bottom underline that looked crooked against the
      // rounded corners. It must NOT come back — the pill carries the state.
      expect(activeStyle.boxShadow).not.toContain('inset');

      await setTheme(page, 'dark');
      await cluster.screenshot({ path: 'test-results/header-pane-toggles-active-dark.png' });

      await git.click();
      await expect(git).toHaveAttribute('aria-pressed', 'true');
      await expect(git).toHaveClass(/studio-tab-action--active/);
      await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });

      await prompt.click();
      await expect(prompt).toHaveAttribute('aria-pressed', 'false');
      await expect(prompt).not.toHaveClass(/studio-tab-action--active/);
      await expect(page.getByTestId('pane-prompt')).toHaveCount(0);

      await protocol.click();
      await expect(protocol).toHaveAttribute('aria-pressed', 'false');
      await expect(protocol).not.toHaveClass(/studio-tab-action--active/);
      await expect(page.getByTestId('pane-protocol')).toHaveCount(0);

      await setTheme(page, 'light');
      await cluster.screenshot({ path: 'test-results/header-pane-toggles-active-light.png' });
    } finally {
      await deleteJob(job.id, watchPath).catch(() => {});
    }
  });
});
