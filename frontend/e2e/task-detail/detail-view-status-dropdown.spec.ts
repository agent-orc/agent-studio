import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';

interface WatchPath { name: string; path: string; }
interface TaskDetail { info: { id: string; state: string } }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

/**
 * Create a hidden fixture task via the canonical /api/tasks route. The
 * shared helpers/jobs.ts still targets the retired /api/tasks route (404),
 * so this spec talks to /api/tasks directly. `fixture: true` keeps the
 * task out of any auto-pick runner.
 */
async function createTask(args: { id: string; title: string; watchPath: string; targetState: string }) {
  return api<{ id: string }>('/api/tasks', {
    method: 'POST',
    body: JSON.stringify({
      id: args.id,
      title: args.title,
      watchPath: args.watchPath,
      agent: 'claude',
      cliType: 'claude',
      targetState: args.targetState,
      fixture: true,
    }),
  });
}

async function getTask(id: string, watchPath: string): Promise<TaskDetail> {
  return api<TaskDetail>(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`);
}

async function deleteTask(id: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
  }).catch(() => { /* best-effort cleanup */ });
}

function uid() {
  return `e2e-status-dd-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

/**
 * Pokes GET /api/tasks/{id} until it returns 200, then resolves. The dev
 * backend can return 404 on the first browser request for a freshly-created
 * fixture; a helper-side GET warms the lookup so the subsequent restoreFromUrl
 * call in the page does not race the indexer.
 */
async function waitForTaskIndexed(id: string, watchPath: string): Promise<void> {
  for (let i = 0; i < 20; i++) {
    try {
      await getTask(id, watchPath);
      return;
    } catch {
      await new Promise(r => setTimeout(r, 250));
    }
  }
  throw new Error(`Task ${id} never became visible to GET /api/tasks/{id}`);
}

/** Records every POST .../move issued while the page is live. */
function trackMoveRequests(page: import('@playwright/test').Page): { count: () => number } {
  let moves = 0;
  page.on('request', req => {
    if (req.method() === 'POST' && /\/api\/v1\/projects\/[^/]+\/tasks\/[^/]+\/move/.test(req.url())) moves += 1;
  });
  return { count: () => moves };
}

/**
 * The studio detail view's lane control must be visibly discoverable as a
 * dropdown: a clear chevron and border on the lane chip, hover/focus
 * feedback, and full keyboard reachability. The control lives in the slim
 * tab-bar header (`studio-lane-select`) because the studio shell hides the
 * projected <app-detail-header>. It is a NAVIGATION control: it pages the
 * detail view through the chosen lane and never moves the current task
 * (moving lives in the ⋯ context menu). This spec covers the affordance
 * contract + the navigation-not-move guarantee; the companion
 * `detail-lane-dropdown` spec covers the page-to-peer behaviour.
 */
test.describe('Detail view — status dropdown (discoverability)', () => {
  test('control is visually distinct as a dropdown', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const id = uid();
    const created = await createTask({
      id,
      title: `status-dropdown-visual ${id}`,
      watchPath: wp.path,
      targetState: '1-preparation',
    });

    try {
      await waitForTaskIndexed(created.id, wp.path);
      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);

      const select = page.getByTestId('studio-lane-select');
      await expect(select).toBeVisible({ timeout: 10_000 });

      // The control has a visible border (not 0px) — the affordance the task
      // was about. We don't lock the exact pixel width; we just assert that
      // the border style is real so a future regression that strips it would
      // be caught.
      const style = await select.evaluate((el) => {
        const cs = getComputedStyle(el as HTMLSelectElement);
        return {
          borderTopWidth: cs.borderTopWidth,
          borderStyle: cs.borderTopStyle,
          cursor: cs.cursor,
          paddingRight: cs.paddingRight,
          appearance: cs.appearance ?? (cs as unknown as { webkitAppearance: string }).webkitAppearance ?? ''
        };
      });
      expect(parseFloat(style.borderTopWidth)).toBeGreaterThanOrEqual(1);
      expect(style.borderStyle).toBe('solid');
      expect(style.cursor).toBe('pointer');
      // Right-side padding leaves room for the chevron — bigger than the
      // default <select> would have, proving the chevron affordance is wired.
      expect(parseFloat(style.paddingRight)).toBeGreaterThanOrEqual(20);
    } finally {
      await deleteTask(created.id, wp.path);
    }
  });

  test('dropdown lists the navigable lanes and hides orchestrator-owned lanes', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const id = uid();
    const created = await createTask({
      id,
      title: `status-dropdown-options ${id}`,
      watchPath: wp.path,
      targetState: '1-preparation',
    });

    try {
      await waitForTaskIndexed(created.id, wp.path);
      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);

      const select = page.getByTestId('studio-lane-select');
      await expect(select).toBeVisible({ timeout: 10_000 });

      const values = await select.evaluate((el) =>
        Array.from((el as HTMLSelectElement).options).map(o => o.value)
      );

      // The lanes the user can page through. The header may include additional
      // lanes (e.g. the current lane if it is non-standard) — this list is the
      // floor, not the ceiling.
      const required = ['1-preparation', '2-ready', '5-human-review', '6-completed', '7-archive'];
      for (const lane of required) {
        expect(values, `lane ${lane} should be navigable in the dropdown`).toContain(lane);
      }

      // In Progress / Auto Review are orchestrator-owned and never selectable
      // (neither as a navigation target nor as a move target).
      expect(values, '3-progress must not be a navigation target').not.toContain('3-progress');
      expect(values, '4-auto-review must not be a navigation target').not.toContain('4-auto-review');
    } finally {
      await deleteTask(created.id, wp.path);
    }
  });

  test('selecting a lane navigates and never issues a move request', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const id = uid();
    const created = await createTask({
      id,
      title: `status-dropdown-nav ${id}`,
      watchPath: wp.path,
      targetState: '1-preparation',
    });

    try {
      await waitForTaskIndexed(created.id, wp.path);
      const moves = trackMoveRequests(page);
      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);

      const select = page.getByTestId('studio-lane-select');
      await expect(select).toBeVisible({ timeout: 10_000 });
      await expect(select).toHaveValue('1-preparation');

      // Pick a different lane. Whether or not that lane has peers to page to,
      // the one thing that must NOT happen is a backend move of THIS task.
      await select.selectOption('6-completed');

      // Give any (erroneous) move request a chance to fire, then assert none did.
      await page.waitForTimeout(1_000);
      expect(moves.count(), 'lane dropdown must not move the task').toBe(0);

      // The task is still where it was — the dropdown is navigation, not a move.
      expect((await getTask(created.id, wp.path)).info.state).toBe('1-preparation');
    } finally {
      await deleteTask(created.id, wp.path);
    }
  });

  test('keyboard: control is focusable and arrows never move the task', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const id = uid();
    const created = await createTask({
      id,
      title: `status-dropdown-keyboard ${id}`,
      watchPath: wp.path,
      targetState: '2-ready',
    });

    try {
      await waitForTaskIndexed(created.id, wp.path);
      const moves = trackMoveRequests(page);
      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);

      const select = page.getByTestId('studio-lane-select');
      await expect(select).toBeVisible({ timeout: 10_000 });
      await expect(select).toHaveValue('2-ready');

      // Direct focus via JS — Tab order in the tab-bar header passes through
      // many controls and is brittle to layout changes; what we actually need
      // to assert is that the control accepts keyboard focus at all, and that
      // arrow keys drive navigation rather than a destructive move.
      await select.focus();
      await expect(select).toBeFocused();

      // Native <select> + ArrowDown selects the next option, which fires the
      // navigation handler. It must never move the task on disk.
      await page.keyboard.press('ArrowDown');
      await page.waitForTimeout(1_000);
      expect(moves.count(), 'arrow-key lane selection must not move the task').toBe(0);
      expect((await getTask(created.id, wp.path)).info.state).toBe('2-ready');
    } finally {
      await deleteTask(created.id, wp.path);
    }
  });
});
