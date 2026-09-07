import { test, expect, Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme, dismissDevErrorDialog } from '../helpers/theme';

/**
 * AGT-2101 — CLI Management restructure acceptance + evidence.
 *
 * The operator review (2026-07-11) asked for:
 *   1. NAMING: the rail entry and the page heading both read "CLI Management".
 *   2. LAYOUT: the CLI catalog is compact stacked ROWS (expandable), leading
 *      with "what's present" (which CLIs, which models, fallback-route state).
 *   3. CAPS stays its own clearly-delineated area on the hub.
 *   4. COMPLETION CONTRACTS carry an explainer head (adapter self-report:
 *      typed yes/no, completion signal, usage source; read-only by design).
 *   5. CLI SESSIONS and CLI PATHS are their own encapsulated rail pages.
 *
 * Fully route-stubbed so it runs against a static/dev frontend with no backend;
 * screenshots are labelled --mocked.
 */

const SHOT_DIR = process.env.RESTRUCTURE_SHOT_DIR ?? 'test-results';

const json = (body: unknown) => async (route: import('@playwright/test').Route) =>
  route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

function modelCatalog(vendor: string) {
  const slug = vendor.toLowerCase();
  return {
    source: 'stubbed',
    models: [
      { id: `${slug}-pro`, label: `${vendor} Pro`, multiplier: 1, vendor, isDefault: true, thinkingLevels: ['low', 'high'] },
      { id: `${slug}-mini`, label: `${vendor} Mini`, multiplier: 0.25, vendor, isDefault: false },
      { id: `${slug}-legacy`, label: `${vendor} Legacy`, multiplier: 1, vendor, isDefault: false, deprecated: true },
    ],
  };
}

function usageReport() {
  const now = new Date().toISOString();
  const session = (id: string, tokens: number) => ({
    id, label: null, updatedAt: now, cwd: `/repos/demo/${id}`,
    lastUsage: tokens ? { tokens, changes: 3, requests: 12 } : null,
    isProjectDefault: false, linkedJob: null,
  });
  return {
    at: now,
    sections: [
      {
        cliType: 'claude', available: true, version: 'claude 1.9.0', path: '/usr/local/bin/claude', error: null,
        projects: [
          { projectName: 'agent-taskboard', rootPath: '/repos/agent-taskboard', sessions: [session('a1', 4200), session('a2', 0)] },
          { projectName: 'coding-agent-chat', rootPath: '/repos/coding-agent-chat', sessions: [session('a3', 1800)] },
        ],
      },
      {
        cliType: 'codex', available: true, version: 'codex 0.4.2', path: '/usr/local/bin/codex', error: null,
        projects: [
          { projectName: 'agent-taskboard', rootPath: '/repos/agent-taskboard', sessions: [session('c1', 900)] },
        ],
      },
      {
        cliType: 'gemini', available: false, version: null, path: null, error: null, projects: [],
      },
    ],
  };
}

function contracts() {
  return [
    { cliType: 'claude', transport: 'stream-json', sessionStartSignal: 'system.init', completionSignal: 'result', failureSignal: 'error', usageSource: 'result.usage', typed: true, notes: 'Typed adapter over the streaming JSON protocol.' },
    { cliType: 'codex', transport: 'jsonl', sessionStartSignal: 'session.created', completionSignal: 'turn.completed', failureSignal: 'turn.failed', usageSource: 'turn.completed.usage', typed: true, notes: 'Typed adapter over JSONL frames.' },
    { cliType: 'gemini', transport: 'text', sessionStartSignal: 'n/a', completionSignal: 'process exit 0', failureSignal: 'non-zero exit', usageSource: 'unavailable', typed: false, notes: 'No typed adapter; exit-based detection.' },
  ];
}

async function stub(page: Page) {
  await page.route('**/api/auth/status', json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/tasks', json([]));
  await page.route('**/api/tasks/grouped*', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/runner/status', json({ projects: {} }));
  await page.route('**/api/runner/token-summary-aggregate*', json({
    projects: 0, orchestratorEntries: 0, orchestratorLlmCalls: 0,
    totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0, totalCacheCreationTokens: 0,
    estimatedApiCostUsd: 0, allModelsPriced: false, byModel: [], byProject: [],
    fetchedAt: new Date().toISOString(), disclaimer: 'stubbed',
  }));
  // One catch-all router for every /api/cli/* endpoint. A single route keyed on
  // the pathname sidesteps all glob-precedence ambiguity between overlapping
  // patterns (quota vs quota/caps vs quota/model-routes vs usage).
  const quotaReport = {
    at: new Date().toISOString(), ttlSeconds: 600,
    snapshots: [
      { cliType: 'claude', fetchedAt: new Date().toISOString(), plan: 'Max 20x', source: 'probe', error: null, windows: [
        { label: '5h session', usedPct: 42, used: 42, limit: 100, unit: '%', resetAt: null, resetLabel: '3h 12m' },
        { label: 'Weekly', usedPct: 71, used: 71, limit: 100, unit: '%', resetAt: null, resetLabel: '4d 6h' },
      ] },
      { cliType: 'codex', fetchedAt: new Date().toISOString(), plan: 'Plus', source: 'probe', error: null, windows: [
        { label: '5h session', usedPct: 12, used: 12, limit: 100, unit: '%', resetAt: null, resetLabel: '1h 40m' },
      ] },
    ],
  };
  const modelRoutes = {
    profiles: {
      claude: {
        cliType: 'claude', primaryModel: 'claude-pro', primaryThinkingLevel: null,
        fallbackCliType: 'codex', fallbackModel: 'codex-pro', fallbackThinkingLevel: null,
        fallbackDisabled: false, fallbackSource: 'override',
      },
      codex: {
        cliType: 'codex', primaryModel: 'codex-pro', primaryThinkingLevel: 'high',
        fallbackCliType: 'claude', fallbackModel: 'claude-pro', fallbackThinkingLevel: 'high',
        fallbackDisabled: false, fallbackSource: 'catalogue',
      },
    },
    activeFallbacks: [{
      primaryCliType: 'codex', effectiveCliType: 'claude', effectiveModel: 'claude-pro',
      effectiveThinkingLevel: 'high', outcome: 'LaunchFallback',
      reason: 'Codex Weekly is at its 98% cap until the 08:38 reset.',
      activatedAt: '2026-09-07T02:16:00Z', resetAt: '2026-09-07T06:38:00Z',
    }],
  };
  await page.route('**/api/cli/**', async (route) => {
    const p = new URL(route.request().url()).pathname;
    let body: unknown = {};
    if (p.endsWith('/quota/model-routes')) body = modelRoutes;
    else if (p.endsWith('/quota/caps')) body = { defaultCapPct: 95, caps: {} };
    else if (p.endsWith('/quota')) body = quotaReport;
    else if (p.endsWith('/usage')) body = usageReport();
    else if (p.endsWith('/contracts')) body = contracts();
    else if (p.endsWith('/models')) {
      const m = /\/api\/cli\/([^/]+)\/models/.exec(p);
      const vendor = m ? m[1].charAt(0).toUpperCase() + m[1].slice(1) : 'CLI';
      body = modelCatalog(vendor);
    } else if (p.includes('/working-memory')) body = { available: false, root: null, capturedAt: new Date().toISOString(), entries: [] };
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.route('**/api/clients', json([]));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/admin/prompts', json({ overrideDirectory: 'stub', items: [] }));
  await page.route('**/api/workspaces*', json([]));
}

async function openHome(page: Page) {
  await page.getByTestId('status-bar-settings').click();
  await expect(page.locator(
    '[data-testid="workspace-settings-inline"], [data-testid="workspace-settings-overlay"]',
  )).toBeVisible({ timeout: 10_000 });
}

test.describe('CLI Management restructure (AGT-2101)', () => {
  // The production build ships an Angular service worker (ngsw) whose dataGroups
  // intercept /api/* once active; that would bypass page.route stubs. Block SWs
  // so every request stays interceptable.
  test.use({ serviceWorkers: 'block' });

  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1500, height: 1000 });
    await page.addInitScript(() => { try { localStorage.setItem('atp.flag.vsCodeLayout', '0'); } catch { /* ignore */ } });
    await stub(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(300);
    await dismissDevErrorDialog(page);
  });

  test('rail entry + page heading both read "CLI Management"; catalog is rows leading with what is present', async ({ page }) => {
    await openHome(page);

    const rail = page.getByTestId('workspace-settings-rail-caps');
    await expect(rail).toContainText('CLI Management');
    await rail.click();

    const overlay = page.getByTestId('cli-admin-overlay');
    await expect(overlay).toBeVisible();
    await expect(overlay.getByRole('heading', { name: 'CLI Management' })).toBeVisible();

    // Leads with the catalog rows (what CLIs / models / routes).
    await expect(overlay.getByTestId('cli-admin-models')).toBeVisible();
    const claudeRow = overlay.getByTestId('cli-models-card-claude');
    await expect(claudeRow).toBeVisible();
    // Collapsed row answers "what's present" at a glance: model count + primary
    // model + fallback-route state, all without expanding.
    await expect(claudeRow).toContainText('3 models');
    await expect(claudeRow.getByTestId('cli-models-primary-summary-claude')).toContainText('Claude Pro');
    await expect(claudeRow).toContainText('→ Codex · Codex Pro');
    const activeFallback = overlay.getByTestId('cli-models-active-fallback-codex');
    await expect(activeFallback).toContainText('Codex → Claude Code · Claude Pro · high');
    await expect(activeFallback).toContainText('Codex Weekly is at its 98% cap until the 08:38 reset.');
    await expect(activeFallback).toContainText('Since');
    // No unexpected app error dialog (would mean an unstubbed endpoint).
    await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);

    // Caps + contracts remain their own sections on the hub.
    await expect(overlay.getByText('Usage caps', { exact: true })).toBeVisible();
    await expect(overlay.getByTestId('cli-admin-contracts-explainer')).toBeVisible();
    await expect(overlay.getByTestId('cli-admin-contracts-explainer')).toContainText('typed adapter');

    // Sessions/paths are no longer embedded here.
    await expect(overlay.getByTestId('cli-admin-sessions')).toHaveCount(0);

    // Expand a row to reveal the route editor + full model list.
    await claudeRow.getByTestId('cli-models-toggle-claude').click();
    await expect(claudeRow.getByTestId('cli-primary-claude')).toBeVisible();
    await expect(claudeRow.getByTestId('cli-fallback-mode-claude')).toHaveValue('override');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await overlay.screenshot({ path: join(SHOT_DIR, `cli-management-hub--mocked-${theme}.png`) });
    }
  });

  test('CLI sessions and CLI paths are their own encapsulated rail pages', async ({ page }) => {
    await openHome(page);

    // Both rail entries exist.
    await expect(page.getByTestId('workspace-settings-rail-cli-sessions')).toBeVisible();
    await expect(page.getByTestId('workspace-settings-rail-cli-paths')).toBeVisible();

    // CLI sessions page.
    await page.getByTestId('workspace-settings-rail-cli-sessions').click();
    await expect(page.getByTestId('cli-sessions-overlay')).toBeVisible();
    const sessions = page.getByTestId('cli-sessions-panel');
    await expect(sessions).toBeVisible();
    // The stubbed inventory renders (confirms the usage report loaded).
    await expect(sessions.getByTestId('cli-sessions-summary')).toContainText('Showing 4 of 4 sessions');
    await expect(sessions.getByTestId('cli-filter-claude')).toContainText('3');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('cli-sessions-overlay').screenshot({ path: join(SHOT_DIR, `cli-sessions-page--mocked-${theme}.png`) });
    }

    // CLI paths page: per-CLI executable + project roots.
    await page.getByTestId('workspace-settings-rail-cli-paths').click();
    await expect(page.getByTestId('cli-paths-overlay')).toBeVisible();
    const paths = page.getByTestId('cli-paths-panel');
    await expect(paths).toBeVisible();
    const claudePaths = paths.getByTestId('cli-paths-row-claude');
    await expect(claudePaths).toBeVisible();
    await expect(claudePaths).toContainText('/usr/local/bin/claude');
    await expect(claudePaths).toContainText('/repos/agent-taskboard');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('cli-paths-overlay').screenshot({ path: join(SHOT_DIR, `cli-paths-page--mocked-${theme}.png`) });
    }
  });
});
