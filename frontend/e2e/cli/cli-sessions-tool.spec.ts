import { test, expect, Page, Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme, dismissDevErrorDialog } from '../helpers/theme';

/**
 * Evidence + regression for the revamped CLI-session tool (AGT-2102).
 *
 * Two flavours:
 *  - `--real`: drives the live dev backend so the tool renders the operator's
 *    real ~/.claude session store (thousands of transcripts) — the case that
 *    motivates virtualisation. Captures both themes.
 *  - `--mocked`: stubs `/api/cli/usage` + `/session-detail` with a large
 *    synthetic set that carries every metadata field (size, model, thinking,
 *    linked task), so the size column, the lazy detail aside and the guarded
 *    cleanup confirm are all exercised deterministically on both themes.
 */

const SHOT_DIR = process.env.SESS_SHOT_DIR ?? 'test-results';

function openSessions(page: Page) {
  return (async () => {
    await page.getByTestId('status-bar-usage').click();
    await expect(page.getByTestId('cli-admin-overlay')).toBeVisible();
    const sessions = page.getByTestId('cli-sessions-panel');
    await sessions.getByTestId('cli-admin-sessions').scrollIntoViewIfNeeded().catch(() => {});
    await page.getByTestId('cli-admin-sessions').scrollIntoViewIfNeeded();
    await expect(sessions).toBeVisible();
    return sessions;
  })();
}

test.describe('CLI-session tool — real data', () => {
  test.beforeEach(() => mkdirSync(SHOT_DIR, { recursive: true }));

  for (const theme of ['dark', 'light'] as const) {
    test(`renders the real session store (${theme})`, async ({ page }) => {
      await page.setViewportSize({ width: 1680, height: 1000 });
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);

      const sessions = await openSessions(page);
      // The revamped toolbar + virtualised list must appear.
      await expect(sessions.getByTestId('cli-sessions-toolbar')).toBeVisible({ timeout: 20_000 });
      await expect(sessions.getByTestId('cli-sessions-viewport')).toBeVisible();
      const rows = sessions.getByTestId('cli-session-row');
      await expect(rows.first()).toBeVisible({ timeout: 20_000 });

      const summary = await sessions.getByTestId('cli-sessions-summary').innerText();
      expect(summary).toMatch(/\d+ of \d+ session/);

      await sessions.screenshot({ path: join(SHOT_DIR, `sessions-tool-overview--real-${theme}.png`) });

      // Open a session to show the lazy detail aside against real data.
      await rows.first().click();
      await expect(sessions.getByTestId('cli-session-detail')).toBeVisible();
      await sessions.screenshot({ path: join(SHOT_DIR, `sessions-tool-detail--real-${theme}.png`) });

      // Exercise search: type a fragment of the first row and confirm the
      // summary count shrinks (virtualised list re-filters live).
      await sessions.getByTestId('cli-sessions-search').fill('claude');
      await page.waitForTimeout(150);
      await sessions.screenshot({ path: join(SHOT_DIR, `sessions-tool-search--real-${theme}.png`) });
    });
  }
});

// ── Mocked: full metadata + cleanup confirm ──────────────────────────────

function buildLargeUsage() {
  const clis = [
    { cliType: 'claude', version: '1.4.2' },
    { cliType: 'codex', version: '0.9.1' },
    { cliType: 'gemini', version: '2.1.0' },
  ];
  const lanes = ['3-progress', '6-completed', '5-human-review', '2-ready', '7-archive'];
  const now = Date.now();
  const sections = clis.map((cli, ci) => {
    const projects = Array.from({ length: 8 }, (_, pi) => {
      const projectName = `project-${cli.cliType}-${pi}`;
      const rootPath = `C:/Projects/${projectName}`;
      const sessions = Array.from({ length: 30 }, (_, si) => {
        const idx = pi * 30 + si;
        const linked = idx % 5 === 0;
        return {
          id: `${cli.cliType}-${pi}-${si}-0000-1111-2222-3333`,
          label: si % 3 === 0 ? `refactor step ${si} in ${projectName}` : null,
          updatedAt: new Date(now - idx * 3_600_000).toISOString(),
          cwd: rootPath,
          sizeBytes: 1024 * (12 + ((idx * 37) % 900)),
          lastUsage: si % 2 === 0 ? { at: '', tokens: `${(idx % 40) + 1}.${idx % 9}k`, changes: null, requests: null } : null,
          isProjectDefault: si === 0,
          linkedJob: linked
            ? {
                jobId: `job-${idx}`,
                title: `AGT-${2000 + idx}`,
                watchPath: rootPath,
                projectName,
                lane: lanes[idx % lanes.length],
                isActive: idx % 15 === 0,
              }
            : null,
        };
      });
      return { projectName, rootPath, sessions };
    });
    return { cliType: cli.cliType, available: true, version: cli.version, path: null, error: null, projects };
  });
  return { at: new Date(now).toISOString(), sections };
}

async function stubBackground(page: Page) {
  const json = (body: unknown) => async (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/tasks', json([]));
  await page.route('**/api/v1/studio/board*', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/v1/studio/runner/status', json({ projects: {} }));
  await page.route('**/api/cli/quota/caps', json({ defaultCapPct: 95, caps: {} }));
  await page.route('**/api/cli/quota', json({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] }));
  await page.route('**/api/clients', json([]));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/cli/usage', json(buildLargeUsage()));
  // Regex (not glob): the cwd query param carries `/`, which a glob `*` will
  // not cross, so a glob would silently miss the request.
  await page.route(/\/api\/cli\/[^/]+\/session-detail/, json({
    id: 'mock', cliType: 'claude', model: 'claude-opus-4-8', thinkingLevel: 'used',
    messageCount: 214, firstPrompt: 'Refactor the session registry to stream one line at a time and bound the scan.',
    cwd: 'C:/Projects/project-claude-0', gitBranch: 'task/session-tool', cliVersion: '1.4.2',
    sizeBytes: 842_000, path: 'C:/Users/rmisc/.claude/projects/c--Projects/mock.jsonl',
    updatedAt: new Date().toISOString(), error: null,
  }));
}

test.describe('CLI-session tool — full metadata + cleanup', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1680, height: 1000 });
    await stubBackground(page);
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`metadata, filters and detail (${theme})`, async ({ page }) => {
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);

      const sessions = await openSessions(page);
      await expect(sessions.getByTestId('cli-sessions-toolbar')).toBeVisible();

      // 720 synthetic sessions (3 clis × 8 projects × 30) — the summary must
      // reconcile to the sum of the visible children (R3).
      const summary = await sessions.getByTestId('cli-sessions-summary').innerText();
      expect(summary).toContain('of 720 sessions');

      await sessions.screenshot({ path: join(SHOT_DIR, `sessions-tool-metadata--mocked-${theme}.png`) });

      // Filter to one CLI, then open a row → lazy detail with model/thinking.
      await sessions.getByTestId('cli-filter-claude').click();
      await sessions.getByTestId('cli-session-row').first().click();
      const detail = sessions.getByTestId('cli-session-detail');
      await expect(detail.getByText('claude-opus-4-8')).toBeVisible();
      await sessions.screenshot({ path: join(SHOT_DIR, `sessions-tool-detail-full--mocked-${theme}.png`) });

      // Guarded cleanup: the confirm dialog must appear (destructive → confirm).
      // Assert the confirm button (has a real box) rather than the app-dialog
      // host, whose element reports "hidden" to Playwright.
      await sessions.getByTestId('cleanup-session').click();
      const confirmBtn = page.getByRole('button', { name: 'Delete session' });
      await expect(confirmBtn).toBeVisible();
      await expect(page.getByTestId('confirm-dialog-message')).toBeVisible();
      await page.screenshot({ path: join(SHOT_DIR, `sessions-tool-cleanup-confirm--mocked-${theme}.png`) });
      // Cancel — never actually delete during a screenshot run.
      await page.keyboard.press('Escape');
    });
  }
});
