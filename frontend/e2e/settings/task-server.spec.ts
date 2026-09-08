import { test, expect, Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * Task Server settings section (AGT-1924).
 *
 * The new "Task Server" section of the consolidated Workspace-settings home is
 * the operator's read-context for the durable task server the platform talks
 * to: the connected URL (localhost today, a central URL in Phase 2), the
 * workspace store it owns, the evidence status, the authoritative host
 * identities, and the management commands.
 *
 * The page renders from the authenticated management API, shared with the
 * server-hosted recovery console. This spec stubs that wire contract and drives
 * the rail. It asserts:
 *   - the rail exposes the "Task Server" section and the overview card;
 *   - the section renders the connection / store / evidence blocks, the client
 *     registry, and the management panel;
 *   - the summary client count reconciles to the visible client rows (R3);
 *   - running a sweep records the API command result;
 *   - a #/workspace/settings/task-server deep-link opens the section;
 *   - the section renders on the light theme too (R5).
 */

const SHOT_DIR = process.env.OVERLAY_SHOT_DIR ?? 'test-results';
const MANAGEMENT_STATUS = {
  server: { id: 'task-server-e2e', url: 'http://localhost:4010', version: '2026.07.20', protocolMinimum: '1.0', protocolMaximum: '1.0', uptimeSeconds: 7200 },
  health: { state: 'healthy', ready: true },
  store: { sizeBytes: 2048, projectCount: 2, taskCount: 12, archivedTaskCount: 8, eventCount: 42, artifactCount: 6, identityCount: 2 },
  evidence: { state: 'available', eventFiles: 4, artifactFiles: 6, lastWriteAt: '2026-07-20T10:00:00Z' },
  maintenance: { mode: 'normal', drainRequested: false, shutdownPrepared: false, reason: null },
  migrations: [] as { id: string; state: string; startedAt: string | null; detail: string | null }[],
  runners: [
    { id: 'runner-1', displayName: 'Runner 1', state: 'running', lastUsedAt: '2026-07-20T10:00:00Z', activeSlots: 1, drainRequested: false, retireRequested: false },
    { id: 'runner-2', displayName: 'Runner 2', state: 'draining', lastUsedAt: '2026-07-20T09:00:00Z', activeSlots: 0, drainRequested: true, retireRequested: false },
  ],
  backups: { directory: '/srv/agent-studio-backups', retentionCount: 7, lastFailure: null as string | null, items: [{ id: 'backup-1', sizeBytes: 1024, createdAt: '2026-07-20T08:00:00Z', verificationState: 'verified' }] },
  security: { available: true, userCount: 2, credentialRunnerCount: 2, sessionUrl: '/api/auth/session', usersUrl: '/api/auth/users', runnerCredentialsUrl: '/api/auth/runners', integration: 'Shared AGT-2193 authority' },
};

function settingsHome(page: Page) {
  return page.locator(
    '[data-testid="workspace-settings-inline"], [data-testid="workspace-settings-overlay"]',
  );
}

async function stubBackgroundApis(page: Page) {
  const json = (body: unknown) => async (route: import('@playwright/test').Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/tasks', json([]));
  await page.route('**/api/auth/status', json({ profile: 'local', bootstrapRequired: false, authenticated: false, user: null }));
  await page.route('**/api/tasks/grouped', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/runner/status', json({ projects: {} }));
  await page.route('**/api/cli/quota', json({ ttlMs: 600_000, snapshots: [] }));
  await page.route('**/api/clients', json([]));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/workspaces*', json([]));
  await page.route('**/api/v1/management/status', json(MANAGEMENT_STATUS));
  await page.route('**/api/v1/management/retention/target', json({
    activeTarget: 's3', copyToSecondary: false, deleteLocalAfterVerification: true,
    localConfigured: true, s3Configured: true, s3Endpoint: 'http://minio:9000',
    s3Bucket: 'agent-studio-archive', s3Prefix: 'studio', deleteArchivedAfterYears: 2,
  }));
  await page.route('**/api/v1/management/retention/plan', json({
    actionCount: 1, totalBytes: 8192, affectedTasks: 1,
    actions: [{ kind: 'DeleteCold', taskKey: 'AGT-1', bytes: 8192 }],
  }));
  await page.route('**/api/v1/management/retention/apply', json({
    runId: 'retention-apply-1', appliedActions: 1, appliedBytes: 8192, errors: [], warnings: [],
  }));
  await page.route('**/api/v1/management/commands', async route => {
    const request = route.request().postDataJSON() as { kind: string; dryRun: boolean };
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      commandId: `cmd-${request.kind}-${request.dryRun ? 'preview' : 'apply'}`,
      kind: request.kind, dryRun: request.dryRun, state: 'completed', matched: 2,
      affected: request.dryRun ? 0 : 2,
      summary: request.dryRun ? '2 items would be changed.' : 'Changed 2 items.',
      completedAt: '2026-07-20T10:30:00Z',
    }) });
  });
}

test.describe('Task Server settings section', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 950 });
    // Force the legacy (modal) layout so the section renders in the modal-backed
    // settings overlay (same choice as workspace-settings-home.spec).
    await page.addInitScript(() => { try { localStorage.setItem('atp.flag.vsCodeLayout', '0'); } catch { /* ignore */ } });
    await stubBackgroundApis(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(400);
    await dismissDevErrorDialog(page);
  });

  test('rail + overview expose the Task Server section', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await expect(settingsHome(page)).toBeVisible();
    await expect(page.getByTestId('workspace-settings-rail-task-server')).toBeVisible();
    await expect(page.getByTestId('workspace-settings-rail-task-server')).toContainText('Task Server');
    await expect(page.getByTestId('workspace-settings-card-task-server')).toBeVisible();
  });

  test('section renders connection, store, evidence, clients, and management', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await dismissDevErrorDialog(page);
    await page.getByTestId('workspace-settings-rail-task-server').click();

    await expect(page.getByTestId('workspace-task-server-overlay')).toBeVisible();
    await expect(page.getByTestId('task-server-panel')).toBeVisible();

    await expect(page.getByTestId('task-server-connection')).toBeVisible();
    await expect(page.getByTestId('task-server-store')).toBeVisible();
    await expect(page.getByTestId('task-server-evidence')).toBeVisible();
    await expect(page.getByTestId('task-server-management')).toBeVisible();
    await expect(page.getByTestId('task-server-clients-section')).toContainText('Host registry');
    await expect(page.getByTestId('task-server-clients-section')).toContainText('Enroll host');

    await expect(page.getByTestId('task-server-url')).toContainText('http://localhost:4010');

    // Summary client count reconciles to the visible client rows (R3).
    const rows = page.locator('[data-testid="task-server-clients"] > li');
    const count = await rows.count();
    expect(count).toBeGreaterThanOrEqual(2);
    await expect(page.getByTestId('task-server-summary')).toContainText(String(count));
    await expect(page.getByTestId('task-server-summary')).toContainText('Hosts');

    await page.screenshot({ path: join(SHOT_DIR, 'task-server-section--mocked.png'), fullPage: false });
  });

  test('running a management sweep records a result row', async ({ page }) => {
    await page.goto('/#/workspace/settings/task-server');
    await expect(page.getByTestId('task-server-panel')).toBeVisible({ timeout: 5_000 });

    await expect(page.getByTestId('task-server-results-empty')).toBeVisible();
    await page.getByTestId('task-server-management-section').scrollIntoViewIfNeeded();
    await page.getByTestId('task-server-action-archive-sweep').click();
    await expect(page.getByTestId('task-server-result-archive-sweep')).toBeVisible({ timeout: 3_000 });
    await expect(page.getByTestId('task-server-confirm-archive-sweep')).toBeVisible();
    await page.getByTestId('task-server-confirm-archive-sweep').click();
    // A second preview so the results list shows more than one settled outcome.
    await page.getByTestId('task-server-action-orphan-sweep').click();
    await expect(page.getByTestId('task-server-result-orphan-sweep')).toBeVisible({ timeout: 3_000 });
    await page.getByTestId('task-server-management-section').scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(SHOT_DIR, 'task-server-management--mocked.png'), fullPage: false });
  });

  test('S3 target status and archive expiry use preview plus typed confirmation', async ({ page }) => {
    await page.goto('/#/workspace/settings/task-server');
    await expect(page.getByTestId('archive-target-status')).toContainText('S3');
    await expect(page.getByTestId('archive-retention-card')).toContainText('2 years');

    await page.getByTestId('archive-delete-preview').click();
    const dialog = page.getByTestId('archive-delete-dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText('1 payloads');
    await expect(page.getByTestId('archive-delete-confirm')).toBeDisabled();
    await page.getByTestId('archive-delete-confirmation').fill('DELETE ARCHIVED PAYLOADS');
    await expect(page.getByTestId('archive-delete-confirm')).toBeEnabled();
    await page.screenshot({ path: join(SHOT_DIR, 'archive-delete-confirmation.png'), fullPage: false });
    await page.getByTestId('archive-delete-confirm').click();
    await expect(dialog).toBeHidden();
  });

  test('deep-link opens the Task Server section directly', async ({ page }) => {
    await page.goto('/#/workspace/settings/task-server');
    await page.waitForLoadState('domcontentloaded');
    await expect(settingsHome(page)).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('workspace-settings-rail-task-server')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('task-server-panel')).toBeVisible();
  });

  test('networked management 401 returns the operator to a concrete sign-in entry', async ({ page }) => {
    await page.unroute('**/api/auth/status');
    await page.route('**/api/auth/status', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        profile: 'networked',
        bootstrapRequired: false,
        authenticated: true,
        user: {
          id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner',
          projects: [], disabled: false, mustChangePassword: false,
        },
      }),
    }));
    await page.unroute('**/api/v1/management/status');
    await page.route('**/api/v1/management/status', route => route.fulfill({
      status: 401,
      contentType: 'application/json',
      body: JSON.stringify({
        error: 'authentication-required',
        message: 'Sign in with an owner or operator account to manage the Task Server.',
        loginUrl: '/api/auth/login',
      }),
    }));

    // Change the document URL, not only the hash, so AuthService reinitializes
    // from the networked status route installed above.
    await page.goto('/?auth-profile=networked#/workspace/settings/task-server');

    await expect(page.getByTestId('auth-gate')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
  });

  test('renders on the light theme too (R5)', async ({ page }) => {
    await page.goto('/#/workspace/settings/task-server');
    await page.waitForLoadState('domcontentloaded');
    await setTheme(page, 'light');
    await expect(page.getByTestId('task-server-panel')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('task-server-connection')).toBeVisible();
    await page.screenshot({ path: join(SHOT_DIR, 'task-server-section-light--mocked.png'), fullPage: false });
  });

  test('captures degraded, maintenance, migration, credential rotation, and failed backup states in both themes', async ({ page }) => {
    const states = [
      { name: 'healthy', patch: {} },
      { name: 'degraded', patch: { health: { state: 'degraded', ready: false } } },
      { name: 'maintenance', patch: { health: { state: 'maintenance', ready: false }, maintenance: { mode: 'maintenance', drainRequested: true, shutdownPrepared: false, reason: 'Operator rehearsal' } } },
      { name: 'migration', patch: { health: { state: 'maintenance', ready: false }, migrations: [{ id: 'schema-42', state: 'running', startedAt: '2026-07-20T10:00:00Z', detail: 'Adding audit index' }] } },
      { name: 'credential-rotation', patch: { runners: MANAGEMENT_STATUS.runners.map((runner, index) => index === 0 ? { ...runner, state: 'credential-rotated' } : runner) } },
      { name: 'failed-backup', patch: { backups: { ...MANAGEMENT_STATUS.backups, lastFailure: 'Archive checksum mismatch' } } },
    ];
    for (const state of states) {
      await page.unroute('**/api/v1/management/status');
      await page.route('**/api/v1/management/status', route => route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ ...MANAGEMENT_STATUS, ...state.patch }),
      }));
      for (const theme of ['dark', 'light'] as const) {
        await page.goto('/#/workspace/settings/task-server');
        await page.reload();
        await setTheme(page, theme);
        await expect(page.getByTestId('task-server-panel')).toBeVisible({ timeout: 5_000 });
        if (state.name === 'credential-rotation') {
          await page.getByTestId('task-server-clients-section').scrollIntoViewIfNeeded();
          await expect(page.getByTestId('task-server-client-runner-1')).toContainText('credential-rotated');
        }
        await page.screenshot({ path: join(SHOT_DIR, `task-server-${state.name}-${theme}.png`), fullPage: false });
      }
    }
  });
});
