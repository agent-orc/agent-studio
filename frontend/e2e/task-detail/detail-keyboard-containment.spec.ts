import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import type { Locator, Page } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { api } from '../helpers/api';
import { createJob, moveJob } from '../helpers/jobs';

interface WatchPath {
  path: string;
}

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function deleteTask(id: string, watchPath: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
  });
}

async function waitForCards(page: Page, ids: readonly string[]): Promise<void> {
  for (const id of ids) {
    await expect(page.getByTestId('task-card').filter({ hasText: id }).first())
      .toBeVisible({ timeout: 20_000 });
  }
}

async function laneOrder(page: Page, ids: readonly string[]): Promise<string[]> {
  const positions = await Promise.all(ids.map(async id => ({
    id,
    y: (await page.getByTestId('task-card').filter({ hasText: id }).first().boundingBox())?.y
      ?? Number.MAX_SAFE_INTEGER,
  })));
  return positions.sort((left, right) => left.y - right.y).map(entry => entry.id);
}

async function installOverflow(surface: Locator): Promise<void> {
  await surface.evaluate(element => {
    const host = element as HTMLElement;
    const scrollOwner = host.querySelector<HTMLElement>('[data-detail-tab-scroll-owner]') ?? host;
    const filler = document.createElement('div');
    filler.dataset['testid'] = 'detail-keyboard-scroll-filler';
    filler.style.height = '2400px';
    filler.style.flex = '0 0 2400px';
    scrollOwner.appendChild(filler);
    scrollOwner.scrollTop = 0;
  });
  await expect.poll(() => surface.evaluate(element => {
    const host = element as HTMLElement;
    const scrollOwner = host.querySelector<HTMLElement>('[data-detail-tab-scroll-owner]') ?? host;
    return scrollOwner.scrollHeight > scrollOwner.clientHeight;
  })).toBe(true);
}

async function scrollTop(surface: Locator): Promise<number> {
  return surface.evaluate(element => {
    const host = element as HTMLElement;
    return (host.querySelector<HTMLElement>('[data-detail-tab-scroll-owner]') ?? host).scrollTop;
  });
}

async function expectTaskOpen(page: Page, id: string): Promise<void> {
  await expect(page.getByRole('heading').filter({ hasText: id }).first()).toBeVisible();
}

async function setTheme(page: Page, theme: 'light' | 'dark'): Promise<void> {
  await page.evaluate(value => {
    document.documentElement.dataset['studioTheme'] = value;
    localStorage.setItem('atp.studio.theme', value);
  }, theme);
}

test.describe('Task detail keyboard containment', () => {
  test('detail tabs own scrolling while the focused task list keeps navigation', async ({ page, devBackend }, testInfo) => {
    void devBackend;
    testInfo.setTimeout(120_000);
    await page.addInitScript(() => {
      localStorage.setItem('atp.flag.vsCodeLayout', '0');
    });

    const watchPath = await pickWatchPath();
    const stamp = `${Date.now()}-${Math.floor(Math.random() * 10_000)}`;
    const ids = [1, 2, 3].map(index => `e2e-keyboard-${stamp}-${index}`);
    // Human review is a protected lane: a code-free concept card reaches it from Ready.
    for (const id of ids) {
      await createJob({
        id,
        title: id,
        watchPath,
        targetState: '2-ready',
        mode: 'concept',
        requiresIntegration: false,
        promptMarkdown: `# ${id}\n\nKeyboard containment fixture.`,
        fixture: false,
      });
      await api(`/api/tasks/${encodeURIComponent(id)}/concept-dossier?watchPath=${encodeURIComponent(watchPath)}`, {
        method: 'POST',
        body: JSON.stringify({ noDossierNeeded: true, reason: 'Code-free end-to-end fixture.' }),
      });
      await moveJob(id, watchPath, '5-human-review');
    }

    try {
      await page.route('**/api/crash-recovery/pending', route => route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ pending: [] }),
      }));
      await page.goto('/');
      await waitForCards(page, ids);
      const ordered = await laneOrder(page, ids);
      const selected = ordered[1];
      await page.getByTestId('task-card').filter({ hasText: selected }).first().click();
      await expectTaskOpen(page, selected);

      const protocolSurface = page.getByTestId('pane-protocol-body');
      const promptSurface = page.getByTestId('pane-prompt-body');
      await expect(protocolSurface).toBeFocused();

      const protocolPane = page.getByTestId('pane-protocol');
      await protocolPane.getByTestId('pane-header-maximize').click();
      await expect(protocolPane.getByRole('button', { name: 'Restore' })).toBeVisible();

      const inspectorCases = [
        { tab: 'inspector-tab-task', surface: protocolSurface },
        { tab: 'inspector-tab-activity', surface: protocolSurface },
        { tab: 'inspector-tab-protocol', surface: protocolSurface },
      ] as const;

      for (const entry of inspectorCases) {
        await page.getByTestId(entry.tab).click();
        await expect(entry.surface).toBeFocused();
        await installOverflow(entry.surface);
        const urlBefore = page.url();
        await page.keyboard.press('ArrowDown');
        await expect.poll(() => scrollTop(entry.surface)).toBeGreaterThan(0);
        expect(page.url()).toBe(urlBefore);
      }

      await protocolPane.getByTestId('pane-header-maximize').click();
      await expect(promptSurface).toBeVisible();

      const promptCases = [
        { tab: 'prompt-tab-timeline', surface: promptSurface },
        { tab: 'prompt-tab-evidence', surface: promptSurface },
      ] as const;

      for (const entry of promptCases) {
        await page.getByTestId(entry.tab).click();
        await expect(entry.surface).toBeFocused();
        await installOverflow(entry.surface);
        const urlBefore = page.url();
        await page.keyboard.press('ArrowDown');
        await expect.poll(() => scrollTop(entry.surface)).toBeGreaterThan(0);
        expect(page.url()).toBe(urlBefore);
      }

      await promptSurface.evaluate(element => {
        const host = element as HTMLElement;
        const scrollOwner = host.querySelector<HTMLElement>('[data-detail-tab-scroll-owner]') ?? host;
        scrollOwner.scrollTop = 300;
      });
      await page.keyboard.press('PageDown');
      const afterPageDown = await scrollTop(promptSurface);
      expect(afterPageDown).toBeGreaterThan(300);
      await page.keyboard.press('PageUp');
      await expect.poll(() => scrollTop(promptSurface)).toBeLessThan(afterPageDown);
      await page.keyboard.press('End');
      const afterEnd = await scrollTop(promptSurface);
      expect(afterEnd).toBeGreaterThan(0);
      await page.keyboard.press('Home');
      await expect.poll(() => scrollTop(promptSurface)).toBe(0);
      await promptSurface.evaluate(element => {
        const host = element as HTMLElement;
        const scrollOwner = host.querySelector<HTMLElement>('[data-detail-tab-scroll-owner]') ?? host;
        scrollOwner.scrollTop = 300;
      });
      await page.keyboard.press('ArrowUp');
      await expect.poll(() => scrollTop(promptSurface)).toBeLessThan(300);
      await expectTaskOpen(page, selected);

      await protocolSurface.focus();
      await page.keyboard.press('Tab');
      await expectTaskOpen(page, selected);

      const taskListSurface = page.locator('.task-nav__group-header').first();
      await taskListSurface.focus();
      const selectedUrl = page.url();
      await page.keyboard.press('ArrowDown');
      await expectTaskOpen(page, ordered[2]);
      expect(page.url()).not.toBe(selectedUrl);

      const resultsDir = resolve(process.env.JOB_RESULTS_DIR ?? testInfo.outputDir);
      await mkdir(resultsDir, { recursive: true });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await protocolSurface.focus();
        await page.screenshot({
          path: resolve(resultsDir, `task-detail-keyboard-containment--${theme}--real.png`),
          fullPage: false,
        });
      }

      await protocolSurface.focus();
      await page.keyboard.press('Escape');
      await expect(page.getByTestId('kanban-dashboard')).toBeVisible();
    } finally {
      for (const id of ids) await deleteTask(id, watchPath).catch(() => undefined);
    }
  });
});
