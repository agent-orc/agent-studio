import { test, expect } from '../fixtures/dev-backend';

/**
 * Global Watcher contingent (AGT-2721, dossier section 10.4).
 *
 * Verifies:
 *   1. The REST surface (`/api/watcher/status`, `/api/watcher/contingent`)
 *      answers with the budget, the per-window usage, and the count of cases
 *      that were found but not drafted.
 *   2. Review mode refuses a decision that cannot be honoured, so a proposal
 *      cannot slip into Ready by accident.
 *   3. The contingent section renders inside Workspace CLI Management next to
 *      the quota strips, and the backlog number is visible there.
 *
 * The backlog is the load-bearing assertion. When the contingent is spent the
 * Watcher keeps detecting and counting but writes no proposals, so an empty
 * proposal inbox must not read as an empty workspace.
 *
 * Uses the `dev-backend` fixture, which is the only sanctioned way to bring
 * dev's backend up (AGENTS.md, "Dev backend lifecycle: Playwright-only").
 */

const CLIENT_ID = 'local-default';

interface ContingentSnapshot {
  budget: Record<string, number>;
  daily: { tokens: number; modelCalls: number; proposals: number; comments: number };
  weekly: { tokens: number; modelCalls: number; proposals: number; comments: number };
  weeklyDollars: number | null;
  exhausted: boolean;
  backlogCases: number;
}

interface WatcherStatus {
  enabled: boolean;
  openCases: number;
  decisionsRequired: number;
  backlog: number;
}

async function api<T>(baseUrl: string, path: string, init: RequestInit = {}): Promise<T> {
  const res = await fetch(`${baseUrl}${path}`, {
    headers: {
      'content-type': 'application/json',
      'x-client-id': CLIENT_ID,
      ...(init.headers ?? {}),
    },
    ...init,
  });
  const text = await res.text();
  if (!res.ok) {
    throw new Error(`API ${init.method ?? 'GET'} ${path} -> ${res.status} ${res.statusText}\n${text}`);
  }
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

test.describe('Global Watcher / contingent and review mode', () => {
  test('GET /api/watcher/status reports the sweep and its decision queue', async ({ devBackend }) => {
    const status = await api<WatcherStatus>(devBackend.baseUrl, '/api/watcher/status');

    expect(typeof status.enabled).toBe('boolean');
    expect(status.openCases).toBeGreaterThanOrEqual(0);
    expect(status.decisionsRequired).toBeGreaterThanOrEqual(0);
    expect(status.backlog).toBeGreaterThanOrEqual(0);
  });

  test('GET /api/watcher/contingent reports the budget and the undrafted backlog', async ({ devBackend }) => {
    const snapshot = await api<ContingentSnapshot>(devBackend.baseUrl, '/api/watcher/contingent');

    expect(snapshot.budget.dailyProposals).toBeGreaterThanOrEqual(0);
    expect(snapshot.budget.weeklyProposals).toBeGreaterThanOrEqual(0);
    expect(snapshot.daily.proposals).toBeGreaterThanOrEqual(0);
    expect(snapshot.weekly.proposals).toBeGreaterThanOrEqual(0);
    expect(snapshot.backlogCases).toBeGreaterThanOrEqual(0);
    expect(typeof snapshot.exhausted).toBe('boolean');
    // Unknown price is carried as null, never coerced to zero.
    expect(snapshot.weeklyDollars === null || typeof snapshot.weeklyDollars === 'number').toBe(true);
  });

  test('a proposal cannot be decided into Ready without existing', async ({ devBackend }) => {
    const refused = await api(
      devBackend.baseUrl,
      '/api/watcher/proposals/WCH-0000-P1/decision',
      { method: 'POST', body: JSON.stringify({ decision: 'approved' }) },
    ).catch(error => error);

    expect(refused).toBeInstanceOf(Error);
    expect(String(refused.message)).toMatch(/does not exist/);
  });

  test('a rejection without a reason is refused', async ({ devBackend }) => {
    const refused = await api(
      devBackend.baseUrl,
      '/api/watcher/proposals/WCH-0000-P1/decision',
      { method: 'POST', body: JSON.stringify({ decision: 'rejected' }) },
    ).catch(error => error);

    expect(refused).toBeInstanceOf(Error);
  });

  test('the contingent and its backlog render in CLI Management', async ({ page }) => {
    await page.goto('/');

    const trigger = page.getByTestId('status-bar-settings');
    await expect(trigger).toBeVisible();
    await trigger.click();

    await page.getByTestId('workspace-settings-rail-caps').click();

    const overlay = page.getByTestId('cli-admin-overlay');
    await expect(overlay).toBeVisible();

    // The section sits next to the quota strips, as the dossier specifies.
    await expect(overlay.getByText('Watcher contingent', { exact: true })).toBeVisible();

    const panel = overlay.getByTestId('watcher-contingent-panel');
    await expect(panel).toBeVisible();

    // The backlog count is what keeps "found but not drafted" visible.
    await expect(panel.getByTestId('watcher-backlog')).toBeVisible();
    await expect(panel.getByTestId('watcher-backlog')).toContainText(
      /cases? found but not drafted/,
    );
  });
});
