import type { Locator, Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';
import { test, expect } from '../fixtures/dev-backend';

/**
 * Execution Hosts settings section (AGT-1921).
 *
 * The "Execution Hosts" section of the consolidated Workspace-settings home
 * lists every execution location - the operator's local machine and each remote
 * runner - in a sortable table with one compact row per host. Identity,
 * capabilities, connection, capacity, and deployment detail is disclosed below
 * the row and its open state survives reloads.
 *
 * Client lifecycle and activity come from the persisted Task Server API.
 *   - the rail exposes the "Execution Hosts" section and the overview card;
 *   - the section renders one primary row per host;
 *   - the header summary count reconciles to the visible rows (R3);
 *   - Drain and graceful Retire call the API, confirm impact, and preserve retired clients;
 *   - a #/workspace/settings/execution-hosts deep-link opens the section.
 */

const SHOT_DIR = process.env.OVERLAY_SHOT_DIR ?? '../results/remote-hosts';
const EVIDENCE_PHASE = process.env.EVIDENCE_PHASE ?? 'after';
const EVIDENCE_VIEWPORT_WIDTH = Number(process.env.EVIDENCE_VIEWPORT_WIDTH ?? 1600);

function settingsHome(page: Page) {
  return page.locator(
    '[data-testid="workspace-settings-inline"], [data-testid="workspace-settings-overlay"]',
  );
}

async function expandHost(host: Locator, summaryOnly = false) {
  const toggle = host.getByTestId('remote-host-disclosure');
  if (await toggle.getAttribute('aria-expanded') !== 'true') await toggle.click();
  await expect(host.getByTestId('remote-host-detail-row')).toBeVisible();
  if (!summaryOnly) {
    const sectionToggles = host.locator('[data-testid^="remote-host-detail-toggle-"]');
    for (let index = 0; index < await sectionToggles.count(); index++) {
      const section = sectionToggles.nth(index);
      if (await section.getAttribute('aria-expanded') !== 'true') await section.click();
    }
  }
}

async function stubBackgroundApis(page: Page) {
  const json = (body: unknown) => async (route: import('@playwright/test').Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/tasks', json([]));
  await page.route('**/api/tasks/grouped', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/auth/status', json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }));
  await page.route('**/api/crash-recovery/pending', json({ pending: [] }));
  await page.route('**/api/watch-paths', json([{ name: 'agent-taskboard', path: 'C:/projects/agent-taskboard', rootPath: 'C:/projects' }]));
  await page.route('**/api/runner/status', json({ projects: {} }));
  await page.route('**/api/runner/queue-starvation', json({
    active: false,
    waitingTaskCount: 0,
    availableSlots: 0,
    thresholdMinutes: 30,
    observedAt: new Date().toISOString(),
    oldestEnteredLaneAt: null,
    items: [],
  }));
  await page.route('**/api/cli/quota', json({ ttlMs: 600_000, snapshots: [] }));
  const now = new Date().toISOString();
  await page.route('**/api/clients', json([
    { id: 'local-default', displayName: 'operator-workstation', kind: 'human', registeredAt: now, lastSeenAt: now },
    { id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service', registeredAt: now, lastSeenAt: now,
      runnerGitStatus: 'ready', runnerGitCheckedAt: now, runnerDaemonState: 'running', runnerActiveSlots: 0, runnerAvailableSlots: 2 },
  ]));
  await page.route('**/api/v1/management/remote-hosts', json([]));
  await page.route('**/api/v1/management/remote-hosts/link-health', json([]));
  await page.route('**/api/clients/*/telemetry?window=*', json({ clientId: 'mock', window: '14d', points: [{
    timestamp: now, cpuPercent: 7, load1: 0.1, load5: 0.1, load15: 0.1,
    memoryUsedBytes: 4_000_000_000, memoryTotalBytes: 16_000_000_000,
    swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0, cpuStealPercent: 0, ioWaitPercent: 0,
    cpuCores: 4, activeSlots: 0,
  }], findings: [] }));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/workspaces*', json([]));
}

async function stubGroupedHostApis(page: Page) {
  const now = new Date();
  const observed = now.toISOString();
  const codingObserved = new Date(now.getTime() - 1_000).toISOString();
  const releaseId = 'agt-2650b-20260812T064049Z-ca5cbd6ff';
  const retired = Array.from({ length: 4 }, (_, index) => ({
    id: `e2e-retired-${index + 1}`,
    displayName: `e2e-retired-${index + 1}`,
    kind: 'retired',
    registeredAt: observed,
    lastSeenAt: observed,
  }));
  const capability = (key: string) => ({
    key,
    category: 'executor',
    advertisedStatus: 'ready',
    healthState: 'healthy',
    advertisedAt: observed,
    freshUntil: new Date(now.getTime() + 180_000).toISOString(),
    isFresh: true,
    consecutiveFailures: 0,
    affectedClaims: [],
    recoveryHistory: [],
  });
  const admission = {
    hostId: 'agent-runner-01',
    admissionState: 'open',
    automaticDrainReason: null,
    automaticDrainAt: null,
    operatorDrainReason: null,
    operatorDrainAt: null,
  };

  await page.unroute('**/api/clients');
  await page.route('**/api/clients', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([
      {
        id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
        registeredAt: observed, lastSeenAt: observed, runnerGitStatus: 'ready',
        runnerDaemonState: 'running', runnerActiveSlots: 0, runnerAvailableSlots: 2,
        runnerProjectPreflights: [{
          projectId: 'PROJ-016', projectName: 'Quality Studio',
          registrationFingerprint: 'a'.repeat(64), repositoryUrl: null,
          fetchUrl: null, pushUrl: null, targetBranch: 'develop', status: 'failed',
          detail: 'repositoryUrl is missing', checkedAt: observed,
        }],
      },
      {
        id: 'agent-runner-01-review', displayName: 'agent-runner-01-review', kind: 'service',
        registeredAt: observed, lastSeenAt: observed, runnerDaemonState: 'running',
        runnerActiveSlots: 0, runnerAvailableSlots: 6,
      },
      ...retired,
    ]),
  }));
  await page.unroute('**/api/clients/*/telemetry?window=*');
  await page.route('**/api/clients/*/telemetry?window=*', route => {
    const review = route.request().url().includes('agent-runner-01-review');
    const timestamp = review ? observed : codingObserved;
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        clientId: review ? 'agent-runner-01-review' : 'agent-runner-01',
        window: '14d',
        findings: [],
        points: [{
          timestamp,
          cpuPercent: review ? 37 : 61,
          load1: 3.7,
          load5: 3.5,
          load15: 3.1,
          memoryUsedBytes: 8_000_000_000,
          memoryTotalBytes: 16_000_000_000,
          swapInBytesPerSecond: 0,
          swapOutBytesPerSecond: 0,
          cpuStealPercent: 0,
          ioWaitPercent: 0,
          cpuCores: 8,
          activeSlots: 0,
        }],
      }),
    });
  });
  await page.unroute('**/api/v1/management/remote-hosts');
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([
      {
        runnerId: 'agent-runner-01',
        name: 'agent-runner-01',
        hostId: 'agent-runner-01',
        instanceId: 'agent-runner-01:coding',
        runnerVersion: releaseId,
        protocolVersion: 3,
        status: 'active',
        registeredAt: observed,
        lastSeenAt: codingObserved,
        hostAdmission: admission,
        capabilities: [
          capability('executor:coding'),
          ...Array.from({ length: 13 }, (_, index) => capability(`tool:${index + 1}`)),
        ],
        telemetry: {
          observedAt: codingObserved,
          cpuPercent: 61,
          memoryUsedBytes: 8_000_000_000,
          memoryTotalBytes: 16_000_000_000,
          cpuCores: 8,
        },
        roleMaxParallelism: 2,
        effectiveMaxParallelism: 2,
      },
      {
        runnerId: 'agent-runner-01-review',
        name: 'agent-runner-01-review',
        hostId: 'agent-runner-01',
        instanceId: 'agent-runner-01:review',
        runnerVersion: releaseId,
        protocolVersion: 3,
        status: 'active',
        registeredAt: observed,
        lastSeenAt: observed,
        hostAdmission: admission,
        capabilities: [capability('executor:review')],
        telemetry: {
          observedAt: observed,
          cpuPercent: 37,
          memoryUsedBytes: 8_000_000_000,
          memoryTotalBytes: 16_000_000_000,
          cpuCores: 8,
        },
        roleMaxParallelism: 6,
        effectiveMaxParallelism: null,
      },
    ]),
  }));
}

test.describe('Execution Hosts settings section', () => {
  test.use({ serviceWorkers: 'block' });

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

  test('rail + overview expose the Execution Hosts section', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await expect(settingsHome(page)).toBeVisible();
    await expect(page.getByTestId('workspace-settings-rail-remote-hosts')).toBeVisible();
    await expect(page.getByTestId('workspace-settings-rail-remote-hosts')).toContainText('Execution Hosts');
    await expect(page.getByTestId('workspace-settings-card-remote-hosts')).toBeVisible();
    await expect(page.getByTestId('workspace-settings-card-remote-hosts')).toContainText('Execution Hosts');
  });

  test('section lists one compact table row per host; summary reconciles to the rows (R3)', async ({ page }) => {
    await page.getByTestId('status-bar-settings').click();
    await dismissDevErrorDialog(page);
    await page.getByTestId('workspace-settings-rail-remote-hosts').click();

    await expect(page.getByTestId('workspace-remote-hosts-overlay')).toBeVisible();
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible();

    const cards = page.getByTestId('remote-host-card');
    const count = await cards.count();
    expect(count).toBeGreaterThanOrEqual(2);
    await expect(cards.filter({ hasText: 'Local machine' })).toHaveCount(1);

    // Core operator truth is visible while secondary detail starts closed.
    await expect(page.getByTestId('remote-host-status').first()).toBeVisible();
    await expect(page.getByTestId('remote-host-load').first()).toBeVisible();
    await expect(page.getByTestId('remote-host-slots-summary').first()).toBeVisible();
    await expect(cards.filter({ hasText: 'agent-runner-01' }).getByTestId('remote-host-action-drain')).toBeVisible();
    await expect(cards.filter({ hasText: 'agent-runner-01' }).getByTestId('remote-host-action-retire')).toBeVisible();
    await expect(page.getByTestId('remote-host-detail-row')).toHaveCount(0);
    const headers = page.getByTestId('remote-hosts-table').locator('thead th');
    await expect(headers).toHaveText([
      /Name/,
      /Status/,
      /Slots/,
      /Load/,
      /Last activity/,
      /Release/,
      /Actions/,
    ]);

    // Header total equals the number of visible cards (R3 sum invariant).
    await expect(page.getByTestId('remote-hosts-summary')).toContainText(String(count));

    await page.screenshot({ path: join(SHOT_DIR, 'remote-hosts-section--mocked.png'), fullPage: false });
  });

  test('sorts table columns and restores sort plus row disclosure after reload', async ({ page }) => {
    await page.goto('/#/workspace/settings/execution-hosts');
    const names = page.getByTestId('remote-host-name');
    await expect(names.first()).toHaveText('agent-runner-01');

    await page.getByTestId('remote-host-sort-name').click();
    await expect(page.getByTestId('remote-hosts-table').locator('th').first())
      .toHaveAttribute('aria-sort', 'descending');
    await expect(names.first()).toHaveText('Local machine');

    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(remote);
    await page.reload();

    await expect(page.getByTestId('remote-hosts-table').locator('th').first())
      .toHaveAttribute('aria-sort', 'descending');
    await expect(page.getByTestId('remote-host-name').first()).toHaveText('Local machine');
    await expect(page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' })
      .getByTestId('remote-host-disclosure')).toHaveAttribute('aria-expanded', 'true');
  });

  test('captures the compact host table in light and dark themes', async ({ page, devBackend: _devBackend }) => {
    await stubGroupedHostApis(page);
    await page.setViewportSize({ width: EVIDENCE_VIEWPORT_WIDTH, height: 950 });
    await page.goto('/#/workspace/settings/execution-hosts');
    await expect(page.getByTestId('remote-hosts-table')).toBeVisible();
    await setTheme(page, 'light');
    await page.screenshot({ path: join(SHOT_DIR, `execution-hosts-${EVIDENCE_PHASE}-light--mocked.png`), fullPage: false });
    await setTheme(page, 'dark');
    await page.screenshot({ path: join(SHOT_DIR, `execution-hosts-${EVIDENCE_PHASE}-dark--mocked.png`), fullPage: false });
  });

  test('groups roles by physical machine, preserves role capacity, release, and retired filtering', async ({ page, devBackend: _devBackend }) => {
    await stubGroupedHostApis(page);
    await page.goto('/#/workspace/settings/execution-hosts');

    const machine = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expect(machine).toHaveCount(1);
    await expect(machine.getByTestId('remote-host-load')).toContainText('37%');
    await expect(machine.getByTestId('remote-host-role-row')).toHaveCount(2);
    await expect(machine.getByTestId('remote-host-role-row').filter({ hasText: 'Coding' })
      .getByTestId('remote-host-slots-summary')).toHaveText(/0 \/ 2/);
    await expect(machine.getByTestId('remote-host-role-row').filter({ hasText: 'Review' })
      .getByTestId('remote-host-slots-summary')).toHaveText(/0 \/ 6/);
    await expect(machine.getByTestId('remote-host-release'))
      .toContainText('agt-2650b-20260812T064049Z-ca5cbd6ff');
    await machine.getByTestId('remote-host-release').hover();
    await expect(page.getByTestId('remote-host-release-tooltip'))
      .toHaveText('agt-2650b-20260812T064049Z-ca5cbd6ff');

    const retiredRows = page.getByTestId('remote-host-role-row').filter({ hasText: 'e2e-retired-' });
    await expect(retiredRows).toHaveCount(0);
    await expect(page.getByTestId('remote-hosts-retired-filter')).toContainText('4');
    await page.getByTestId('remote-hosts-retired-filter').click();
    await expect(retiredRows).toHaveCount(4);

    const local = page.getByTestId('remote-host-card').filter({ hasText: 'Local machine' });
    await expect(local.getByTestId('remote-host-load')).toHaveText('–');
    await expect(local.getByTestId('remote-host-release')).toHaveText('–');
  });

  test('narrow tables collapse complete actions into the row overflow menu', async ({ page, devBackend: _devBackend }) => {
    await page.setViewportSize({ width: 900, height: 820 });
    await stubGroupedHostApis(page);
    await page.goto('/#/workspace/settings/execution-hosts');

    const coding = page.getByTestId('remote-host-role-row').filter({ hasText: 'Coding' });
    await expect(coding.getByTestId('remote-host-action-drain')).toBeHidden();
    await expect(coding.getByTestId('remote-host-action-overflow')).toBeVisible();
    await coding.getByTestId('remote-host-action-overflow').click();
    await expect(coding.getByRole('menuitem', { name: 'Drain' })).toBeVisible();
    await expect(coding.getByRole('menuitem', { name: 'Retire' })).toBeVisible();
    await expect.poll(() => page.getByTestId('remote-hosts-table').evaluate(
      table => table.scrollWidth <= table.clientWidth + 1,
    )).toBe(true);

    await setTheme(page, 'light');
    await page.screenshot({ path: join(SHOT_DIR, 'execution-hosts-after-narrow-light--mocked.png'), fullPage: false });
    await setTheme(page, 'dark');
    await page.screenshot({ path: join(SHOT_DIR, 'execution-hosts-after-narrow-dark--mocked.png'), fullPage: false });
  });

  test('expanded machine starts with compact section summaries and reveals one section at a time', async ({ page, devBackend: _devBackend }) => {
    await stubGroupedHostApis(page);
    await page.goto('/#/workspace/settings/execution-hosts');

    const machine = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(machine, true);
    const summaries = machine.locator('[data-testid^="remote-host-detail-toggle-"]');
    await expect(summaries).toHaveCount(7);
    await expect(machine.getByTestId('remote-host-detail-toggle-capabilities'))
      .toContainText('14 capabilities ok');
    await expect(machine.getByTestId('remote-host-detail-toggle-projects'))
      .toContainText('1 project block');
    await expect(machine.locator('[aria-label="Capabilities"]')).toHaveCount(0);

    await machine.getByTestId('remote-host-detail-toggle-projects').click();
    const projectBlock = machine.getByTestId('remote-host-project-preflight-failures');
    await expect(projectBlock).toContainText('Quality Studio');
    await expect(projectBlock).toContainText('repositoryUrl is missing');
    await expect(projectBlock.getByRole('link', { name: 'Open project' }))
      .toHaveAttribute('href', '#/projects/PROJ-016');
    await expect(machine.locator('[aria-label="Capabilities"]')).toHaveCount(0);

    await setTheme(page, 'light');
    await machine.screenshot({ path: join(SHOT_DIR, 'execution-hosts-expanded-summary-light--mocked.png') });
    await setTheme(page, 'dark');
    await machine.screenshot({ path: join(SHOT_DIR, 'execution-hosts-expanded-summary-dark--mocked.png') });
  });

  test('shows a corrupt identity with its restore path in both themes', async ({ page }) => {
    await page.unroute('**/api/clients');
    await page.route('**/api/clients', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        {
          id: 'local-default', displayName: 'operator-workstation', kind: 'human',
          registeredAt: new Date().toISOString(), lastSeenAt: new Date().toISOString(),
        },
        {
          id: 'agent-runner-01',
          displayName: 'agent-runner-01',
          kind: 'service',
          registeredAt: '2026-08-05T14:35:00Z',
          lastSeenAt: null,
          identityFileError: 'identity file corrupt: agent-runner-01.json',
          identityFileName: 'agent-runner-01.json',
          identityFileModifiedAt: '2026-08-05T14:35:00Z',
          identityFileSizeBytes: 4481,
          identityRestoreHint: 'Restore this file from a known-good backup or Git revision, or re-register the original displayName with POST /api/clients/register.',
        },
      ]),
    }));
    await page.goto('/#/workspace/settings/remote-hosts');

    const diagnostic = page.getByTestId('remote-hosts-identity-errors');
    await expect(diagnostic).toBeVisible();
    await expect(diagnostic).toContainText('identity file corrupt: agent-runner-01.json');
    await expect(diagnostic).toContainText('POST /api/clients/register');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expect(remote.getByTestId('remote-host-status')).toContainText('Offline');

    await setTheme(page, 'light');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-identity-corrupt-light--mocked.png'), fullPage: false });
    await setTheme(page, 'dark');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-identity-corrupt-dark--mocked.png'), fullPage: false });
  });

  test('first mount waits for live status and then paints the daemon without reload', async ({ page }) => {
    let releaseResponse!: () => void;
    const responseGate = new Promise<void>(resolve => { releaseResponse = resolve; });
    const now = new Date().toISOString();
    await page.unroute('**/api/clients');
    await page.unroute('**/api/clients/*/telemetry?window=*');
    await page.route('**/api/clients', async route => {
      await responseGate;
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
        id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
        registeredAt: now, lastSeenAt: now, runnerGitStatus: 'ready',
        runnerDaemonState: 'running', runnerActiveSlots: 1, runnerAvailableSlots: 19,
      }]) });
    });
    await page.route('**/api/clients/agent-runner-01/telemetry?window=*', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({
        clientId: 'agent-runner-01', window: '14d', findings: [], points: [{
          timestamp: now, cpuPercent: 53, load1: 7.7, load5: 7.1, load15: 6.8,
          memoryUsedBytes: 34_000_000_000, memoryTotalBytes: 64_000_000_000,
          swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0, cpuStealPercent: 0,
          ioWaitPercent: 2.1, cpuCores: 12, activeSlots: 1,
        }],
      }),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expect(remote.getByTestId('remote-host-status')).toContainText('Loading live status');
    await expect(remote).not.toContainText('Daemonstopped');

    releaseResponse();

    await expect(remote.getByTestId('remote-host-status')).toContainText('Online');
    await expandHost(remote);
    await expect(remote.getByTestId('remote-host-activity')).toContainText('Daemonrunning');
    await expect(remote.getByTestId('remote-host-run-pool')).toContainText('1 active');
    await expect(remote.getByTestId('remote-host-gate-pool')).toHaveCount(0);
    await expect(remote.getByTestId('remote-host-vitals')).toContainText('53%');
    await expect(remote.getByTestId('remote-host-slots-context')).toContainText('1 RUN active · host load 7.7 of 12 cores');
    await remote.scrollIntoViewIfNeeded();
    await setTheme(page, 'light');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-live-first-mount-light--mocked.png'), fullPage: false });
    await setTheme(page, 'dark');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-live-first-mount-dark--mocked.png'), fullPage: false });
  });

  test('shows and centrally updates the host runtime capacity', async ({ page }) => {
    const now = new Date().toISOString();
    let capacityBody: Record<string, unknown> | null = null;
    let projectPolicyBody: Record<string, unknown> | null = null;
    await page.unroute('**/api/v1/management/remote-hosts');
    await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        runnerId: 'agent-runner-01',
        name: 'agent-runner-01',
        hostId: 'runner-host-a',
        instanceId: 'runner-host-a:1',
        runnerVersion: '1.0.0',
        protocolVersion: 3,
        status: 'active',
        registeredAt: now,
        lastSeenAt: now,
        hostAdmission: {
          hostId: 'runner-host-a',
          admissionState: 'open',
          automaticDrainReason: null,
          automaticDrainAt: null,
          operatorDrainReason: null,
          operatorDrainAt: null,
        },
        capabilities: [],
        telemetry: null,
        runtimeCapacity: {
          hostId: 'runner-host-a',
          maxParallelism: 4,
          targetLoadPercent: 80,
          rampStrategy: 'balanced',
          version: 1,
          updatedAt: now,
        },
        effectiveMaxParallelism: 4,
        runtimeCapacityAppliedAt: now,
        runtimeCapacityAppliedVersion: 1,
        projectPolicy: {
          hostId: 'runner-host-a',
          allowAllProjects: true,
          allowedProjectIds: [],
          version: 1,
          updatedAt: now,
        },
      }]),
    }));
    await page.route('**/api/v1/hosts/runner-host-a/runtime-capacity', async route => {
      capacityBody = route.request().postDataJSON();
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          hostId: 'runner-host-a',
          maxParallelism: 6,
          targetLoadPercent: 85,
          rampStrategy: 'aggressive',
          version: 2,
          updatedAt: now,
        }),
      });
    });
    await page.route('**/api/v1/hosts/runner-host-a/project-policy', async route => {
      projectPolicyBody = route.request().postDataJSON();
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          hostId: 'runner-host-a',
          allowAllProjects: false,
          allowedProjectIds: ['PROJ-001', 'PROJ-002'],
          version: 2,
          updatedAt: now,
        }),
      });
    });

    await page.goto('/#/workspace/settings/execution-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(remote);
    await expect(remote.getByTestId('remote-host-release')).toHaveText('1.0.0');
    await expect(remote.getByTestId('remote-host-slots')).toContainText('0 active / 4 free / 4 total');
    await remote.getByTestId('remote-host-capacity-input').fill('6');
    await remote.getByTestId('remote-host-target-load-input').fill('85');
    await remote.getByTestId('remote-host-ramp-select').selectOption('aggressive');
    await remote.getByTestId('remote-host-capacity-save').click();

    await expect.poll(() => capacityBody).toEqual({
      maxParallelism: 6,
      targetLoadPercent: 85,
      rampStrategy: 'aggressive',
      expectedVersion: 1,
    });
    await expect(remote.getByTestId('remote-host-slots')).toContainText('0 active / 6 free / 6 total');
    await expect(remote.getByTestId('remote-host-capacity-awaiting-adoption')).toBeVisible();
    await remote.getByTestId('remote-host-project-policy-mode').selectOption('selected');
    await remote.getByTestId('remote-host-project-policy-ids').fill('PROJ-001, PROJ-002');
    await remote.getByTestId('remote-host-project-policy-save').click();
    await expect.poll(() => projectPolicyBody).toEqual({
      allowAllProjects: false,
      allowedProjectIds: ['PROJ-001', 'PROJ-002'],
      expectedVersion: 1,
    });
    await expect(remote.getByTestId('remote-host-project-policy'))
      .toContainText('Allowed projects: PROJ-001, PROJ-002');
    await expect(remote.getByTestId('remote-host-project-policy-save')).toBeEnabled();
    await setTheme(page, 'dark');
    await remote.screenshot({ path: join(SHOT_DIR, 'runtime-capacity-dark--mocked.png') });
    await setTheme(page, 'light');
    await remote.screenshot({ path: join(SHOT_DIR, 'runtime-capacity-light--mocked.png') });
  });

  test('Drain and graceful Retire require confirmation and keep a revivable retired client', async ({ page }) => {
    let kind = 'service';
    let draining = false;
    const now = new Date().toISOString();
    await page.unroute('**/api/clients');
    await page.route('**/api/clients/*/drain', async route => { draining = true; await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }); });
    await page.route('**/api/clients/*/retire', async route => { draining = true; kind = 'retired'; await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }); });
    await page.route('**/api/clients/*/revive', async route => { draining = false; kind = 'service'; await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }); });
    await page.route('**/api/clients', async route => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
        id: 'agent-runner-01', displayName: 'agent-runner-01', kind, registeredAt: now, lastSeenAt: now,
        drainRequestedAt: draining ? now : null, retireRequestedAt: draining ? now : null,
        runnerGitStatus: 'ready', runnerDaemonState: kind === 'retired' ? 'stopped' : 'running', runnerActiveSlots: 0, runnerAvailableSlots: 2,
      }]) });
    });
    await page.goto('/#/workspace/settings/remote-hosts');
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible();

    let card = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expect(card.getByTestId('remote-host-action-drain')).toHaveAttribute('title', /No new leases|Stop new leases/);
    await card.getByTestId('remote-host-action-drain').click();
    await expect(card.getByTestId('remote-host-status')).toContainText('Draining');

    await card.getByTestId('remote-host-action-retire').click();
    await expect(page.getByTestId('remote-host-confirm')).toContainText('No new leases');
    await expect(page.getByTestId('remote-host-confirm')).toContainText('remains visible and can be revived');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-retire-confirm-dark.png'), fullPage: false });
    await page.getByTestId('remote-host-confirm-submit').click();
    card = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expect(card).toHaveCount(0);
    await page.getByTestId('remote-hosts-retired-filter').click();
    card = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expect(card).toHaveAttribute('data-retired', 'true');
    await expect(card.getByTestId('remote-host-status')).toContainText('Retired');
    await card.scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-retired-revive-dark.png'), fullPage: false });
    await card.getByTestId('remote-host-action-revive').click();
    await expect(page.getByTestId('remote-hosts-active').getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' })).toBeVisible();
  });

  test('configures one host and starts setup on the durable CLI task substrate', async ({ page }) => {
    let createBody: Record<string, unknown> | null = null;
    const providerSecret = 'sk-ant-oat01-playwright-provider-secret';
    await page.unroute('**/api/tasks');
    await page.route('**/api/tasks', async route => {
      if (route.request().method() !== 'POST') {
        await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
        return;
      }
      createBody = route.request().postDataJSON();
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: 'onboard-runner-02' }) });
    });
    await page.route('**/api/tasks/onboard-runner-02**', route => route.fulfill({
      status: 404,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'mocked-task-detail-not-mounted' }),
    }));
    await page.route('**/api/v1/management/remote-hosts/provider-auth', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        provider: 'claude', environmentVariable: 'CLAUDE_CODE_OAUTH_TOKEN',
        host: 'agent-runner', state: 'installed-awaiting-runner',
        detail: 'The protected EnvironmentFile was installed.', requestedAt: new Date().toISOString(),
        restartedServices: [], processEnvironmentVerified: false,
      }),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(remote);
    await remote.getByTestId('remote-host-action-setup').click();

    await expect(page.getByTestId('runner-setup-dialog')).toBeVisible();
    await expect(page.getByTestId('runner-setup-loopback-block')).toContainText('Loopback is not remotely reachable');
    await expect(page.getByTestId('visible-cli-task-card')).toBeHidden();

    await page.getByTestId('runner-setup-git-remote').fill('https://github.com/example/agent-studio.git');
    await page.getByTestId('runner-setup-git-push-remote').fill('git@github.com:example/agent-studio.git');
    await page.getByTestId('runner-setup-connection-mode').selectOption('tunnel');
    await page.getByTestId('runner-setup-provider-auth-secret').fill(providerSecret);
    await page.getByTestId('runner-setup-provider-auth-provision').click();
    await expect(page.getByTestId('runner-setup-provider-auth-secret')).toHaveValue('');
    await expect(page.getByTestId('runner-setup-provider-auth-status')).toHaveAttribute('data-state', 'waiting');

    await expect(page.getByTestId('visible-cli-task-card')).toBeVisible();
    await expect(page.getByTestId('visible-cli-task-prompt')).toContainText('Reachability gate (must run first)');
    await expect(page.getByTestId('visible-cli-task-prompt')).toContainText('/etc/agent-runner/provider-auth.env');
    await expect(page.getByTestId('visible-cli-task-prompt')).not.toContainText(providerSecret);
    await expect(page.getByTestId('visible-cli-task-duration')).toContainText('10 to 20 minutes');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-runner-setup--mocked.png'), fullPage: false });
    await page.getByTestId('visible-cli-task-start').click();

    await expect.poll(() => createBody).not.toBeNull();
    expect(createBody).toMatchObject({
      title: 'Set up agent host on agent-runner-01',
      agent: 'codex',
      targetState: '2-ready',
      watchPath: 'C:/projects/agent-taskboard',
    });
    expect(String(createBody?.['promptMarkdown'])).toContain('## CLI input');
    expect(String(createBody?.['promptMarkdown'])).toContain('bash scripts/remote-runner-onboard.sh');
    expect(String(createBody?.['promptMarkdown'])).toContain("--host 'agent-runner'");
    expect(String(createBody?.['promptMarkdown'])).toContain('X-Client-Id: agent-runner-01');
    expect(String(createBody?.['promptMarkdown'])).toContain('Provider credentials were already delivered by Studio through SSH stdin');
    expect(String(createBody?.['promptMarkdown'])).not.toContain(providerSecret);
  });

  test('shows provider auth retrying, limited, expiring, signed-out, and unknown states', async ({ page }) => {
    const now = Date.now();
    const capability = (key: string, advertisedStatus: string, detail?: string) => ({
      key, category: key.split(':')[0], advertisedStatus, healthState: 'healthy',
      reason: null, advertisedAt: new Date(now - 30_000).toISOString(),
      freshUntil: new Date(now + 120_000).toISOString(), isFresh: true,
      firstFailureAt: null, lastFailureAt: null, cooldownUntil: null,
      canaryClaimId: null, consecutiveFailures: 0, version: null,
      identity: key.split(':')[1], detail, affectedClaims: [], recoveryHistory: [],
    });
    const claude = {
      ...capability('provider-auth:claude', 'unavailable', 'Not logged in'),
      signal: 'signed-out',
      recoveryHistory: [{
        occurredAt: new Date(now - 30_000).toISOString(), fromState: 'ready',
        toState: 'unavailable', reason: 'Provider probe changed.',
      }],
    };
    const codex = {
      ...capability('provider-auth:codex', 'ready', 'Transient auth error, retrying after a token refresh race.'),
      signal: 'transient-auth-error',
    };
    const copilot = {
      ...capability('provider-auth:copilot', 'limited', 'Rate-limited until the provider reset.'),
      signal: 'rate-limited', limitedUntil: new Date(now + 15 * 60_000).toISOString(),
    };
    const antigravity = {
      ...capability('provider-auth:antigravity', 'ready', 'Active session confirmed; credentials expire soon.'),
      signal: 'credentials-expiring',
      expiresAt: new Date(now + 10 * 24 * 60 * 60_000).toISOString(),
    };
    await page.unroute('**/api/v1/management/remote-hosts');
    await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        runnerId: 'agent-runner-01', name: 'agent-runner-01', hostId: 'host-berlin',
        instanceId: 'coding', runnerVersion: '1.2.0', protocolVersion: 2, status: 'active',
        registeredAt: new Date(now - 86_400_000).toISOString(), lastSeenAt: new Date(now).toISOString(),
        hostAdmission: { hostId: 'host-berlin', admissionState: 'open' },
        capabilities: [
          capability('cli-execution:claude', 'ready'), claude,
          capability('cli-execution:codex', 'ready'), codex,
          copilot, antigravity,
          capability('cli-execution:gemini', 'ready'),
        ],
        telemetry: null,
      }]),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(remote);
    await expect(remote.getByTestId('remote-host-provider-auth-claude')).toHaveAttribute('data-state', 'unavailable');
    await expect(remote.getByTestId('remote-host-provider-auth-codex')).toHaveAttribute('data-state', 'retrying');
    await expect(remote.getByTestId('remote-host-provider-auth-copilot')).toHaveAttribute('data-state', 'limited');
    await expect(remote.getByTestId('remote-host-provider-auth-antigravity')).toHaveAttribute('data-state', 'expiring');
    await expect(remote.getByTestId('remote-host-provider-auth-gemini')).toHaveAttribute('data-state', 'unknown');
    await expect(remote.getByTestId('remote-host-provider-auth-expiry-antigravity')).toContainText('Expires in 10 days');
    await expect(remote.getByTestId('remote-host-provider-auth-history-claude')).toContainText('ready → unavailable');
    await remote.getByTestId('remote-host-provider-auth-claude').hover();
    await expect(page.getByRole('tooltip')).toContainText('Not logged in');

    await setTheme(page, 'dark');
    await remote.screenshot({ path: join(SHOT_DIR, 'provider-auth-states-dark--mocked.png') });
    await setTheme(page, 'light');
    await remote.screenshot({ path: join(SHOT_DIR, 'provider-auth-states-light--mocked.png') });
  });

  test('surfaces a failed startup push probe as a read-only host', async ({ page }) => {
    await page.unroute('**/api/clients');
    await page.route('**/api/clients', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
        registeredAt: '2026-07-11T19:00:00Z', lastSeenAt: new Date().toISOString(),
        runnerGitStatus: 'read-only', runnerGitDetail: 'push-dry-run failed (128): permission denied',
      }]),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(remote);
    const badge = remote.getByTestId('remote-host-git-status');
    await expect(badge).toBeVisible();
    await expect(badge).toContainText('Fallback repo: blocked');
    await badge.hover();
    await expect(page.getByRole('tooltip')).toContainText('permission denied');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-read-only--mocked.png'), fullPage: false });
  });

  test('shows missing workflow permission with a concrete token fix and keeps inflow open', async ({ page }) => {
    await page.unroute('**/api/clients');
    await page.route('**/api/clients', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
        registeredAt: '2026-07-26T09:00:00Z', lastSeenAt: new Date().toISOString(),
        runnerGitStatus: 'ready-no-workflow-scope',
        runnerGitDetail: 'contents ready; GitHub workflow scope missing',
      }]),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(remote);
    await expect(remote.getByTestId('remote-host-git-status')).toContainText('Fallback repo: ok');
    await expect(remote.getByTestId('remote-host-workflow-status'))
      .toContainText('Fallback workflow: permission missing');
    const fix = remote.getByTestId('remote-host-token-scope-fix');
    await expect(fix).toContainText('update both credential URL forms');
    await expect(fix.getByRole('link', { name: 'Open token requirements' }))
      .toHaveAttribute(
        'href',
        'https://github.com/agent-orc/agent-studio/blob/main/docs/operations/setup/linux-runner-host.md#token-requirements',
      );
    await expect(remote.getByTestId('remote-host-activity')).toContainText('Task inflowopen');
    await setTheme(page, 'light');
    await page.screenshot({
      path: join(SHOT_DIR, 'remote-host-workflow-scope--light--mocked.png'),
      fullPage: false,
    });
    await setTheme(page, 'dark');
    await page.screenshot({
      path: join(SHOT_DIR, 'remote-host-workflow-scope--dark--mocked.png'),
      fullPage: false,
    });
  });

  test('shows a stale Task Server route as a visible host outage in both themes', async ({ page }) => {
    const now = Date.now();
    const failureStartedAt = new Date(now - 6 * 60_000).toISOString();
    await page.unroute('**/api/v1/management/remote-hosts');
    await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        runnerId: 'agent-runner-01', name: 'agent-runner-01', hostId: 'host-berlin',
        instanceId: 'review', runnerVersion: '1.2.0', protocolVersion: 2,
        status: 'active', registeredAt: new Date(now - 86_400_000).toISOString(),
        lastSeenAt: failureStartedAt,
        hostAdmission: {
          hostId: 'host-berlin', admissionState: 'open', automaticDrainReason: null,
          automaticDrainAt: null, operatorDrainReason: null, operatorDrainAt: null,
        },
        capabilities: [{
          key: 'task-server:connectivity', category: 'foundation', advertisedStatus: 'ready',
          healthState: 'healthy', reason: null, advertisedAt: failureStartedAt,
          freshUntil: new Date(now - 3 * 60_000).toISOString(), isFresh: false,
          firstFailureAt: null, lastFailureAt: null, cooldownUntil: null,
          canaryClaimId: null, consecutiveFailures: 0, version: null,
          identity: '127.0.0.1:15031', detail: 'Task Server route reachable before the outage.',
          affectedClaims: [], recoveryHistory: [],
        }],
        telemetry: {
          observedAt: failureStartedAt, cpuPercent: 7, memoryUsedBytes: 4_000_000_000,
          memoryTotalBytes: 16_000_000_000, cpuCores: 6,
          taskServerConnectionStatus: 'unreachable',
          taskServerConnectionObservedAt: failureStartedAt,
          taskServerConnectionFailureStartedAt: failureStartedAt,
          taskServerConnectionConsecutiveFailures: 61,
          taskServerConnectionEscalatedAt: new Date(now - 60_000).toISOString(),
          taskServerConnectionLastError: 'connection refused',
        },
      }]),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(remote);
    await expect(remote.getByTestId('remote-host-task-server-route')).toContainText('unreachable');
    const route = remote.getByTestId('remote-host-task-server-route-state');
    await expect(route).toContainText('Task Server route unreachable');
    await expect(route).toContainText('No connectivity advertisement has arrived');
    await expect(route).toContainText('Check the tunnel');

    await setTheme(page, 'dark');
    await remote.screenshot({ path: join(SHOT_DIR, 'task-server-route-outage-dark--mocked.png') });
    await setTheme(page, 'light');
    await remote.screenshot({ path: join(SHOT_DIR, 'task-server-route-outage-light--mocked.png') });
  });

  test('shows selective capability drain, canary context, affected claims, and recovery history without freshening stale metrics', async ({ page }) => {
    const now = Date.now();
    await page.unroute('**/api/v1/management/remote-hosts');
    await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        runnerId: 'agent-runner-01',
        name: 'agent-runner-01',
        hostId: 'host-berlin',
        instanceId: 'coding-codex',
        runnerVersion: '1.2.0',
        protocolVersion: 2,
        status: 'active',
        registeredAt: new Date(now - 86_400_000).toISOString(),
        lastSeenAt: new Date(now - 15_000).toISOString(),
        hostAdmission: {
          hostId: 'host-berlin',
          admissionState: 'open',
          automaticDrainReason: null,
          automaticDrainAt: null,
          operatorDrainReason: null,
          operatorDrainAt: null,
        },
        capabilities: [{
          key: 'provider-auth:codex',
          category: 'provider-auth',
          advertisedStatus: 'ready',
          healthState: 'draining',
          reason: 'ProviderUnauthorized: Codex returned 401',
          advertisedAt: new Date(now - 30_000).toISOString(),
          freshUntil: new Date(now + 120_000).toISOString(),
          isFresh: true,
          firstFailureAt: new Date(now - 90_000).toISOString(),
          lastFailureAt: new Date(now - 30_000).toISOString(),
          cooldownUntil: new Date(now + 90_000).toISOString(),
          canaryClaimId: 'run_canary',
          consecutiveFailures: 2,
          version: 'available',
          identity: 'codex',
          affectedClaims: ['run:run_active', 'review:review_active'],
          recoveryHistory: [{
            occurredAt: new Date(now - 30_000).toISOString(),
            fromState: 'suspect',
            toState: 'draining',
            reason: 'Codex returned 401',
            claimId: 'run_active',
          }],
        }],
        telemetry: {
          observedAt: new Date(now - 10 * 60_000).toISOString(),
          cpuPercent: 99,
          memoryUsedBytes: 15_000_000_000,
          memoryTotalBytes: 16_000_000_000,
          cpuCores: 4,
          diskFreeBytes: 1,
          diskTotalBytes: 100,
        },
      }]),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const card = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(card);
    const capability = card.getByTestId('remote-host-capability-provider-auth:codex');
    await expect(capability).toContainText('draining');
    await expect(capability).toContainText('Codex returned 401');
    await expect(capability).toContainText('run_canary');
    await expect(capability).toContainText('run:run_active, review:review_active');
    await capability.getByText('Recovery history · 1').click();
    await expect(capability).toContainText('suspect → draining');
    await expect(card.getByTestId('remote-host-vitals')).not.toContainText('99%');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-capability-drain--mocked.png'), fullPage: false });
  });

  test('labels automatic whole-host drain separately from operator-requested drain', async ({ page }) => {
    const now = new Date().toISOString();
    await page.unroute('**/api/v1/management/remote-hosts');
    await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        runnerId: 'agent-runner-01', name: 'agent-runner-01', hostId: 'host-berlin',
        instanceId: 'coding', runnerVersion: '1.2.0', protocolVersion: 2,
        status: 'active', registeredAt: now, lastSeenAt: now,
        hostAdmission: {
          hostId: 'host-berlin', admissionState: 'automatic-draining',
          automaticDrainReason: 'host:disk: DiskFull', automaticDrainAt: now,
          operatorDrainReason: 'planned maintenance', operatorDrainAt: now,
        },
        capabilities: [],
        telemetry: null,
      }]),
    }));
    await page.goto('/#/workspace/settings/remote-hosts');
    const card = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(card);
    const admission = card.getByTestId('remote-host-admission');
    await expect(admission).toContainText('Automatic whole-host drain');
    await expect(admission).toContainText('host:disk');
    await expect(admission).not.toContainText('Operator-requested');
    const operatorAdmission = card.getByTestId('remote-host-operator-admission');
    await expect(operatorAdmission).toContainText('Operator-requested host drain');
    await expect(operatorAdmission).toContainText('planned maintenance');
    await expect(operatorAdmission).not.toContainText('Automatic');
  });

  test('shows the missing repository URL claim refusal in Remote Hosts', async ({ page }) => {
    await page.unroute('**/api/clients');
    await page.route('**/api/clients', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
        registeredAt: '2026-07-22T10:00:00Z', lastSeenAt: new Date().toISOString(),
        runnerGitStatus: 'ready',
        runnerProjectPreflights: [{
          projectId: 'PROJ-042', projectName: 'Payments', registrationFingerprint: 'a'.repeat(64),
          repositoryUrl: null, fetchUrl: null, pushUrl: null, status: 'failed',
          detail: 'Remote execution is not claimable: repositoryUrl is missing; repository URL is not configured.',
          checkedAt: '2026-08-08T10:01:00Z',
        }],
      }]),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(remote);
    const failure = remote.getByTestId('remote-host-project-preflight-failures');
    await expect(failure).toContainText('Payments');
    await expect(failure).toContainText('repositoryUrl is missing');
    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await remote.screenshot({
        path: join(SHOT_DIR, `remote-host-repository-warning-${theme}--mocked.png`),
      });
    }
  });

  test('shows persisted performance history, slot context, and a throttling finding', async ({ page }) => {
    await page.unroute('**/api/clients');
    await page.route('**/api/clients', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify([{
        id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
        registeredAt: '2026-07-11T18:00:00Z', lastSeenAt: new Date().toISOString(),
      }]),
    }));
    const now = Date.now();
    const points = Array.from({ length: 8 }, (_, index) => ({
      timestamp: new Date(now - (7 - index) * 30_000).toISOString(),
      cpuPercent: 44 + index * 3, load1: 5.7 + index * 0.1, load5: 5.4, load15: 5.1,
      memoryUsedBytes: 36_000_000_000 + index * 300_000_000, memoryTotalBytes: 64_000_000_000,
      swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0, cpuStealPercent: 6.2, ioWaitPercent: 2.1,
      cpuCores: 12, activeSlots: index < 4 ? 5 : 6,
    }));
    await page.route('**/api/clients/agent-runner-01/telemetry?window=*', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({
        clientId: 'agent-runner-01', window: '14d', points,
        findings: [
          { kind: 'vm-throttled', label: 'VM throttled', since: points[0].timestamp,
            until: points.at(-1)?.timestamp, occurrences: 1, isActive: true },
          { kind: 'oversubscribed', label: 'Oversubscribed', since: points[1].timestamp,
            until: points.at(-1)?.timestamp, occurrences: 1, isActive: true },
          { kind: 'memory-pressure', label: 'Memory pressure', since: points[0].timestamp,
            until: points[2].timestamp, occurrences: 3, isActive: false },
          { kind: 'oversubscribed', label: 'Oversubscribed', since: points[0].timestamp,
            until: points[1].timestamp, occurrences: 2, isActive: false },
          { kind: 'vm-throttled', label: 'VM throttled', since: points[0].timestamp,
            until: points[1].timestamp, occurrences: 4, isActive: false },
        ],
      }),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(remote);
    await expect(remote.getByTestId('remote-host-telemetry')).toBeVisible();
    await expect(remote.getByTestId('remote-host-slots-context')).toContainText('6 RUN active · host load 6.4 of 12 cores');
    await expect(remote.getByTestId('remote-host-findings')).toContainText('VM throttled');
    await expect(remote.getByTestId('remote-host-finding')).toHaveCount(3);
    await expect(remote.getByTestId('remote-host-findings')).toContainText('3× in window');
    await expect(remote.getByTestId('remote-host-findings-more')).toHaveText('+2 more');
    const findingsBox = await remote.getByTestId('remote-host-findings').boundingBox();
    const cardBox = await remote.boundingBox();
    expect(findingsBox).not.toBeNull();
    expect(cardBox).not.toBeNull();
    expect(findingsBox!.x + findingsBox!.width).toBeLessThanOrEqual(cardBox!.x + cardBox!.width);
    await expect(remote.locator('[data-chart]')).toHaveCount(4);

    const inspectedIndex = 4;
    const plots = remote.getByTestId('remote-host-telemetry-plots');
    await plots.scrollIntoViewIfNeeded();
    const bounds = await plots.boundingBox();
    expect(bounds).not.toBeNull();
    await plots.hover({ position: {
      x: bounds!.width * inspectedIndex / (points.length - 1),
      y: bounds!.height / 2,
    } });
    const tooltip = remote.getByTestId('remote-host-telemetry-tooltip');
    await expect(tooltip).toBeVisible();
    await expect(tooltip).toHaveAttribute('data-point-timestamp', points[inspectedIndex].timestamp);
    await expect(tooltip.locator('[data-metric="cpu"]')).toHaveText('56%');
    await expect(tooltip.locator('[data-metric="memory"]')).toHaveText('37.2 GB');
    await expect(tooltip.locator('[data-metric="load"]')).toHaveText('6.1 load');
    await expect(tooltip.locator('[data-metric="slots"]')).toHaveText('6 slots');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-telemetry-tooltip--mocked.png'), fullPage: false });
    await setTheme(page, 'light');
    await expect(tooltip).toBeVisible();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-telemetry-tooltip-light--mocked.png'), fullPage: false });
    await setTheme(page, 'dark');

    await remote.getByTestId('remote-host-window-1h').click();
    await expect(remote.getByTestId('remote-host-window-1h')).toHaveAttribute('aria-pressed', 'true');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-telemetry--mocked.png'), fullPage: false });
  });

  test('adds a host through the guided five-step setup including deploy key', async ({ page }) => {
    let providerInstalled = false;
    await page.unroute('**/api/v1/management/remote-hosts');
    await page.route('**/api/v1/management/remote-hosts/provider-auth', route => {
      providerInstalled = true;
      return route.fulfill({
        status: 200, contentType: 'application/json', body: JSON.stringify({
          provider: 'claude', environmentVariable: 'CLAUDE_CODE_OAUTH_TOKEN',
          host: 'runner@host.example.com', state: 'awaiting-probe',
          detail: 'Daemon environment verified.', requestedAt: new Date().toISOString(),
          restartedServices: ['agent-host.service'], processEnvironmentVerified: true,
        }),
      });
    });
    await page.route('**/api/v1/management/remote-hosts', route => {
      const now = new Date();
      return route.fulfill({
        status: 200, contentType: 'application/json', body: JSON.stringify(providerInstalled ? [{
          runnerId: 'agent-runner-02', name: 'agent-runner-02', hostId: 'agent-runner-02',
          instanceId: 'coding', runnerVersion: '1.2.0', protocolVersion: 2, status: 'active',
          registeredAt: now.toISOString(), lastSeenAt: now.toISOString(),
          hostAdmission: { hostId: 'agent-runner-02', admissionState: 'open' },
          capabilities: [{
            key: 'provider-auth:claude', category: 'provider-auth', advertisedStatus: 'ready',
            healthState: 'healthy', reason: null, advertisedAt: now.toISOString(),
            freshUntil: new Date(now.getTime() + 120_000).toISOString(), isFresh: true,
            firstFailureAt: null, lastFailureAt: null, cooldownUntil: null, canaryClaimId: null,
            consecutiveFailures: 0, version: null, identity: 'claude', detail: 'Active session confirmed',
            affectedClaims: [], recoveryHistory: [],
          }], telemetry: null,
        }] : []),
      });
    });
    await page.goto('/#/workspace/settings/remote-hosts');
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible({ timeout: 5_000 });
    await page.getByTestId('remote-hosts-add').click();
    await expect(page.getByTestId('add-host-wizard')).toBeVisible();

    await page.getByTestId('add-host-connect-check').check();
    await page.getByTestId('add-host-next').click();
    await page.getByTestId('add-host-provision-check').check();
    await page.getByTestId('add-host-next').click();
    await expect(page.getByTestId('add-host-wizard')).toContainText('write-enabled repository deploy key');
    await page.getByTestId('add-host-deploy-key-check').check();
    await page.getByTestId('add-host-next').click();
    await page.getByTestId('add-host-provider-auth-secret').fill('sk-ant-oat01-playwright-provider-secret');
    await page.getByTestId('add-host-provider-auth-provision').click();
    await expect(page.getByTestId('add-host-provider-auth-status')).toHaveAttribute('data-state', 'ok');
    await page.getByTestId('add-host-codex-check').check();
    await setTheme(page, 'dark');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-add-wizard-provider-auth-dark--mocked.png'), fullPage: false });
    await setTheme(page, 'light');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-add-wizard-provider-auth-light--mocked.png'), fullPage: false });
    await setTheme(page, 'dark');
    await page.getByTestId('add-host-next').click();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-add-wizard--mocked.png'), fullPage: false });
    await page.getByTestId('add-host-smoke-check').check();
    await page.getByTestId('add-host-next').click();

    await expect(page.getByTestId('add-host-wizard')).toBeHidden();
    await expect(page.getByTestId('remote-host-name').filter({ hasText: 'agent-runner-02' })).toBeVisible();
  });

  test('renders on the light theme too (R5)', async ({ page }) => {
    await page.goto('/#/workspace/settings/remote-hosts');
    await page.waitForLoadState('domcontentloaded');
    await setTheme(page, 'light');
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('remote-host-card').first()).toBeVisible();
    await page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-hosts-section-light--mocked.png'), fullPage: false });

    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(remote);
    await remote.getByTestId('remote-host-action-setup').click();
    await expect(page.getByTestId('runner-setup-dialog')).toBeVisible();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-runner-setup-light--mocked.png'), fullPage: false });
  });

  test('never renders stale CPU as live and captures dark-theme evidence', async ({ page }) => {
    await page.unroute('**/api/clients');
    await page.route('**/api/clients', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
      id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service', registeredAt: '2026-07-09T09:00:00Z',
      lastSeenAt: '2026-07-09T09:00:00Z', runnerGitStatus: 'ready', runnerGitCheckedAt: '2026-07-09T09:00:00Z',
      runnerDaemonState: 'running', runnerActiveSlots: 2, runnerAvailableSlots: 0,
    }]) }));
    await page.goto('/#/workspace/settings/remote-hosts');
    await setTheme(page, 'dark');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(remote);
    await expect(remote.getByTestId('remote-host-stale')).toContainText('last slot sample is marked stale');
    await expect(remote.getByTestId('remote-host-vitals')).toHaveCount(0);
    await expect(remote).not.toContainText('54%');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-hosts-stale-dark.png'), fullPage: false });
  });

  test('shows a down runner link, raises one keeper notification, and reconnects it', async ({ page }) => {
    const lastSnapshotAt = '2026-09-06T08:00:00Z';
    let reconnectCalls = 0;
    const downLink = {
      runnerId: 'agent-runner-01', name: 'agent-runner-01', linkState: 'down',
      lastSnapshotAt, stateSince: '2026-09-06T08:03:00Z', snapshotAgeSeconds: 7200,
      readyCardsTargetHost: true,
      keeper: {
        supported: true, taskName: 'AgentRunner-TunnelKeeper', state: 'unhealthy',
        enabled: false, running: false, sshRunning: false, cause: 'task-disabled',
        observedAt: '2026-09-06T08:00:00Z',
        logTail: ['2026-09-06T08:00:00Z status=unreachable'],
        detail: 'The Scheduled Task is disabled.',
      },
    };
    await page.unroute('**/api/v1/management/remote-hosts/link-health');
    await page.route('**/api/v1/management/remote-hosts/link-health', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify([downLink]),
    }));
    await page.route('**/api/v1/management/remote-hosts/agent-runner-01/reconnect', route => {
      reconnectCalls++;
      return route.fulfill({
        status: 200, contentType: 'application/json', body: JSON.stringify({
          runnerId: 'agent-runner-01', succeeded: true, enabled: true, started: true,
          detail: 'Enabled and started AgentRunner-TunnelKeeper.', linkState: 'down',
          nextSnapshotAgeSeconds: 7201,
          keeper: { ...downLink.keeper, state: 'healthy', enabled: true, running: true, sshRunning: true, cause: null },
        }),
      });
    });

    await page.goto('/#/workspace/settings/remote-hosts');
    const runnerRole = page.getByTestId('remote-host-role-row').filter({ hasText: 'agent-runner-01' }).first();
    await expect(runnerRole.getByTestId('remote-host-link-state')).toContainText('Down since 2026-09-06T08:03:00Z');
    const warning = page.getByTestId('notification-warning').filter({ hasText: 'agent-runner-01 link is down' });
    await expect(warning).toContainText('Scheduled Task is disabled');
    await expect(page.getByTestId('notification-warning')).toHaveCount(1);
    await expect(page.getByTestId('remote-host-link-notification')).toBeVisible();
    await expect(page.getByTestId('remote-host-link-notification')).toContainText('Scheduled Task is disabled');
    await setTheme(page, 'dark');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-link-down-notification--mocked.png'), fullPage: false });

    await page.getByTestId('remote-host-link-reconnect').click();
    await expect.poll(() => reconnectCalls).toBe(1);
    await expect(page.getByTestId('notification-success')).toContainText('reconnect started');
  });

  test('dims a stale active-slot sample while a fresh heartbeat remains online', async ({ page }) => {
    const now = new Date();
    const staleSample = new Date(now.getTime() - 10 * 60_000).toISOString();
    await page.unroute('**/api/clients');
    await page.unroute('**/api/clients/*/telemetry?window=*');
    await page.route('**/api/clients', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 'agent-runner-01',
        displayName: 'agent-runner-01',
        kind: 'service',
        registeredAt: now.toISOString(),
        lastSeenAt: now.toISOString(),
        runnerGitStatus: 'ready',
        runnerDaemonState: 'running',
        runnerActiveSlots: 3,
        runnerAvailableSlots: 0,
      }]),
    }));
    await page.route('**/api/clients/agent-runner-01/telemetry?window=*', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        clientId: 'agent-runner-01',
        window: '14d',
        findings: [],
        points: [{
          timestamp: staleSample,
          cpuPercent: 53,
          load1: 4.2,
          load5: 4,
          load15: 3.8,
          memoryUsedBytes: 34_000_000_000,
          memoryTotalBytes: 64_000_000_000,
          swapInBytesPerSecond: 0,
          swapOutBytesPerSecond: 0,
          cpuStealPercent: 0,
          ioWaitPercent: 1,
          cpuCores: 12,
          activeSlots: 3,
        }],
      }),
    }));

    await page.goto('/#/workspace/settings/remote-hosts');
    const remote = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' });
    await expandHost(remote);
    const runPool = remote.getByTestId('remote-host-run-pool');
    await expect(remote.getByTestId('remote-host-status')).toContainText('Online');
    await expect(runPool).toContainText('3 active · stale');
    await expect(runPool).toHaveClass(/workload--stale/);
    await expect(remote.getByTestId('remote-host-slots-context')).toHaveClass(/telemetry__context--stale/);
    await setTheme(page, 'dark');
    await remote.scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-slots-stale-dark--mocked.png'), fullPage: false });
    await setTheme(page, 'light');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-slots-stale-light--mocked.png'), fullPage: false });
  });

  test('deep-link opens the Execution Hosts section directly', async ({ page }) => {
    await page.goto('/#/workspace/settings/execution-hosts');
    await page.waitForLoadState('domcontentloaded');
    await expect(settingsHome(page)).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('workspace-settings-rail-remote-hosts')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('remote-hosts-panel')).toBeVisible();
    await expect(page.getByTestId('remote-hosts-panel').getByRole('heading', { level: 2 }))
      .toHaveText('Execution Hosts');
  });
});
