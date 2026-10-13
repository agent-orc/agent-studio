import type { Locator, Page, Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';
import { test, expect } from '../fixtures/dev-backend';

/**
 * Execution Hosts credential view (AGT-2978, I8 of docs/credentials-and-logins/index.html).
 *
 * The Task Server projects the role-scoped view; these scenarios mock that projection
 * with redacted metadata and prove what Studio renders for it:
 *   - login failure, provider incident, quota, network and pending diagnosis stay separate;
 *   - an incident shows its scheduled retry and offers no action;
 *   - a confirmed invalid native Claude login starts the native (R2) host flow;
 *   - a 7-day reminder and unknown expiry render as such;
 *   - a viewer sees health only; a narrow screen stays readable without horizontal scroll.
 */

const SHOT_DIR = resolve(process.env.JOB_RESULTS_DIR ?? '../results', 'execution-host-credentials');
const HOST = 'agent-runner-01';

type Role = 'owner' | 'operator' | 'viewer';

function json(body: unknown) {
  return (route: Route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function stubBackgroundApis(page: Page) {
  const now = new Date().toISOString();
  await page.route('**/api/tasks', json([]));
  await page.route(/\/api\/v1\/studio\/board(?:\?|$)/, json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/v1/studio/auth/status', json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }));
  await page.route('**/api/crash-recovery/pending', json({ pending: [] }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/v1/studio/runner/status', json({ projects: {} }));
  await page.route('**/api/runner/queue-starvation', json({
    active: false, waitingTaskCount: 0, availableSlots: 0, thresholdMinutes: 30, observedAt: now, oldestEnteredLaneAt: null, items: [],
  }));
  await page.route('**/api/cli/quota', json({ ttlMs: 600_000, snapshots: [] }));
  await page.route('**/api/clients', json([
    { id: 'local-default', displayName: 'operator-workstation', kind: 'human', registeredAt: now, lastSeenAt: now },
    { id: HOST, displayName: HOST, kind: 'service', registeredAt: now, lastSeenAt: now,
      runnerGitStatus: 'ready', runnerGitCheckedAt: now, runnerDaemonState: 'running', runnerActiveSlots: 0, runnerAvailableSlots: 2 },
  ]));
  await page.route('**/api/v1/management/remote-hosts', json([]));
  await page.route('**/api/v1/management/links', json([]));
  await page.route('**/api/v1/management/host-releases', json({
    observedAt: now, stable: { version: '0.3.0', commit: null, builtAt: null }, behindCount: 0, hosts: [],
  }));
  await page.route('**/api/v1/management/provider-refusals?days=14', json([]));
  await page.route('**/api/v1/management/runner-infrastructure-failures', json([]));
  await page.route('**/api/clients/*/telemetry?window=*', json({ clientId: HOST, window: '14d', points: [], findings: [] }));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/v1/workspaces*', json([]));
}

const unknownDue = {
  knowledge: 'unknown', subject: null, dueAt: null, thresholdDays: null, overdue: false,
  expiryUnknown: true, reviewDueAt: null, unknownReason: 'issuer-unknown',
};

function row(now: number, role: Role, overrides: Record<string, unknown>) {
  const mayRead = role !== 'viewer';
  return {
    installationId: 'installation', hostId: HOST, credentialId: 'claude-native', kind: 'claude_native_login',
    provider: 'claude', renewalMethod: 'R2', adapter: 'native-cli-store', effectiveSource: 'active',
    localRef: mayRead ? 'service-user native store' : null, scopes: ['host:agent-runner-01'],
    services: ['coding', 'review'], owner: 'Runner provider owner', generation: 'gen-3f2a', supersedes: null,
    lastVerifiedAt: new Date(now - 3_600_000).toISOString(), lastRealSuccessAt: new Date(now - 3_600_000).toISOString(),
    observedAt: new Date(now - 60_000).toISOString(), status: 'healthy', statusDetail: null, nextRetryAt: null,
    serviceAvailability: 'available', evidenceQuality: 'ok', due: unknownDue, reminder: null, healthItem: null,
    runbookHref: 'docs/operations/setup/cli-relogin-runbook.md',
    evidenceRefs: mayRead ? ['evidence:probe-real-request'] : [], actions: [],
    ...overrides,
  };
}

function credentialView(role: Role) {
  const now = Date.now();
  const operator = role !== 'viewer';
  const owner = role === 'owner';
  const dueIn = (days: number) => new Date(now + days * 86_400_000).toISOString();
  return {
    role, generatedAt: new Date(now).toISOString(),
    rows: [
      row(now, role, {
        status: 'credential_invalid', statusDetail: 'login-confirmed-invalid', serviceAvailability: 'unavailable',
        lastVerifiedAt: new Date(now - 2 * 86_400_000).toISOString(),
        healthItem: { kind: 'renewal', openedAt: new Date(now - 600_000).toISOString(), runbookHref: 'docs/operations/setup/cli-relogin-runbook.md' },
        actions: operator ? [
          { kind: 'renew', label: 'Sign in to Claude on this host', primary: true, method: 'R2' },
          { kind: 'runbook', label: 'Open renewal runbook', primary: false, href: 'docs/operations/setup/cli-relogin-runbook.md' },
        ] : [],
      }),
      row(now, role, {
        credentialId: 'claude-token', kind: 'claude_oauth_token', renewalMethod: 'R1', adapter: 'environment-file',
        due: { knowledge: 'known', subject: 'expiry', dueAt: dueIn(5.5), thresholdDays: 7, overdue: false,
          expiryUnknown: false, reviewDueAt: null, unknownReason: null },
        reminder: { generation: 'gen-71c0', subject: 'expiry', dueAt: dueIn(5.5), thresholdDays: 7,
          openedAt: new Date(now - 8 * 86_400_000).toISOString(), updatedAt: new Date(now - 86_400_000).toISOString() },
        effectiveSource: 'shadowed',
        actions: operator ? [{ kind: 'renew', label: 'Renew Claude token on this host', primary: false, method: 'R1' }] : [],
      }),
      row(now, role, {
        credentialId: 'codex-login', kind: 'codex_chatgpt_login', provider: 'codex', renewalMethod: 'R3',
        status: 'provider_incident', statusDetail: 'retry-scheduled', nextRetryAt: new Date(now + 60_000).toISOString(),
        serviceAvailability: 'unavailable',
      }),
      row(now, role, {
        credentialId: 'api-key', kind: 'provider_api_key', provider: 'claude', renewalMethod: 'R8', adapter: 'environment-file',
        status: 'quota_exhausted', statusDetail: 'provider-limit', nextRetryAt: new Date(now + 45 * 60_000).toISOString(),
        serviceAvailability: 'limited', owner: 'Provider account administrator',
      }),
      row(now, role, {
        credentialId: 'deploy-key', kind: 'github_deploy_key', provider: 'github', renewalMethod: 'R5',
        adapter: 'ssh-private-key', owner: 'Repository service owner', services: [], scopes: ['repository:workspace'],
        serviceAvailability: null, status: 'network_failure', statusDetail: 'connectivity',
        due: { knowledge: 'known', subject: 'rotation', dueAt: dueIn(12), thresholdDays: 14, overdue: false,
          expiryUnknown: false, reviewDueAt: null, unknownReason: null },
        reminder: { generation: 'gen-key2', subject: 'rotation', dueAt: dueIn(12), thresholdDays: 14,
          openedAt: new Date(now - 2 * 86_400_000).toISOString(), updatedAt: new Date(now - 2 * 86_400_000).toISOString() },
        actions: owner ? [{ kind: 'runbook', label: 'Open rotation runbook', primary: false, href: 'docs/operations/setup/cli-relogin-runbook.md' }]
          : operator ? [{ kind: 'requires-administrator', label: 'An administrator renews this credential', primary: false }] : [],
      }),
      row(now, role, {
        credentialId: 'review-codex', kind: 'codex_chatgpt_login', provider: 'codex', renewalMethod: 'R3',
        status: 'diagnosis_pending', statusDetail: 'evidence-inconclusive', serviceAvailability: 'unavailable',
        evidenceQuality: 'clock-skew', lastVerifiedAt: null,
        actions: operator ? [{ kind: 'runbook', label: 'Open diagnosis runbook', primary: false, href: 'docs/operations/setup/cli-relogin-runbook.md' }] : [],
      }),
    ],
  };
}

async function openCredentials(page: Page, role: Role): Promise<Locator> {
  await page.route('**/api/v1/management/credential-view', json(credentialView(role)));
  await page.goto('/#/workspace/settings/remote-hosts');
  await dismissDevErrorDialog(page);
  const host = page.getByTestId('remote-host-card').filter({ hasText: HOST });
  const disclosure = host.getByTestId('remote-host-disclosure');
  if (await disclosure.getAttribute('aria-expanded') !== 'true') await disclosure.click();
  const section = host.getByTestId('remote-host-detail-section-credentials');
  await expect(section.getByTestId('remote-host-detail-toggle-credentials')).toContainText('6 credentials · 3 need attention');
  await section.getByTestId('remote-host-detail-toggle-credentials').click();
  await expect(section.getByTestId('host-credentials')).toBeVisible();
  return section;
}

async function shoot(page: Page, target: Locator, name: string) {
  // The settings modal caps its height; a second shot scrolls the last credential into view.
  const last = target.locator('[data-testid^="host-credential-"][data-status]').last();
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await target.locator('[data-testid^="host-credential-"][data-status]').first().scrollIntoViewIfNeeded();
    await target.screenshot({ path: join(SHOT_DIR, `${name}-${theme}--mocked.png`) });
    await last.scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(SHOT_DIR, `${name}-lower-${theme}--mocked.png`) });
  }
}

test.describe('Execution Hosts credential view', () => {
  test.use({ serviceWorkers: 'block' });
  test.describe.configure({ timeout: 480_000 });

  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    // Tall enough that the whole Credentials section fits in the evidence screenshots.
    await page.setViewportSize({ width: 1600, height: 2200 });
    await page.addInitScript(() => { try { localStorage.setItem('atp.flag.vsCodeLayout', '0'); } catch { /* ignore */ } });
    await stubBackgroundApis(page);
  });

  test('operator sees separated states and the host-specific renewal for a confirmed invalid login', async ({ page }) => {
    let startBody: Record<string, unknown> | null = null;
    await page.route('**/api/v1/management/remote-hosts/*/claude-sign-in', async route => {
      startBody = route.request().postDataJSON() as Record<string, unknown>;
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        handle: 'claude_fake_session', state: 'pending', verificationUrl: 'https://claude.ai/login/mock',
        expiresAt: new Date(Date.now() + 900_000).toISOString(),
      }) });
    });
    await page.route('**/api/v1/management/remote-hosts/*/claude-sign-in/*', json({
      handle: 'claude_fake_session', state: 'pending', detail: 'Waiting for browser sign-in.',
      requestedAt: new Date().toISOString(), expiresAt: new Date(Date.now() + 900_000).toISOString(), completedAt: null,
    }));

    const section = await openCredentials(page, 'operator');
    await expect(section.getByTestId('host-credential-claude-native')).toHaveAttribute('data-status', 'credential_invalid');
    await expect(section.getByTestId('host-credential-codex-login')).toHaveAttribute('data-status', 'provider_incident');
    await expect(section.getByTestId('host-credential-status-claude-native')).toContainText('Login invalid · renewal needed');
    await expect(section.getByTestId('host-credential-service-claude-native')).toContainText('Service unavailable');
    await expect(section.getByTestId('host-credential-status-codex-login')).toContainText('Provider incident · retry scheduled');
    await expect(section.getByTestId('host-credential-actions-codex-login')).toHaveCount(0);
    await expect(section.getByTestId('host-credential-status-api-key')).toContainText('Quota exhausted · resets');
    await expect(section.getByTestId('host-credential-actions-api-key')).toHaveCount(0);
    await expect(section.getByTestId('host-credential-status-deploy-key')).toContainText('Network failure');
    await expect(section.getByTestId('host-credential-status-review-codex')).toContainText('Diagnosis pending');
    await expect(section.getByTestId('host-credential-renew-review-codex')).toHaveCount(0);
    await expect(section.getByTestId('host-credential-quality-review-codex')).toContainText('Clock skew');
    await expect(section.getByTestId('host-credential-due-claude-token')).toContainText(/Expires in [56] days/);
    await expect(section.getByTestId('host-credential-reminder-claude-token')).toContainText('7-day reminder');
    await expect(section.getByTestId('host-credential-due-claude-native')).toContainText('Expiry unknown');
    await expect(section.getByTestId('host-credential-due-deploy-key')).toContainText('Rotation due in 12 days');
    await expect(section.getByTestId('host-credential-admin-deploy-key')).toContainText('An administrator renews this credential');
    await expect(section.getByTestId('host-credential-runbook-claude-native'))
      .toHaveAttribute('href', 'https://github.com/agent-orc/agent-studio/blob/main/docs/operations/setup/cli-relogin-runbook.md');
    await expect(section.getByRole('button', { name: /reveal|download|copy/i })).toHaveCount(0);
    await expect(section.getByRole('link', { name: /reveal|download|copy/i })).toHaveCount(0);
    await expect(section).toContainText('Secret values are never shown, downloaded or copied.');

    await section.getByTestId('host-credential-toggle-claude-native').click();
    const detail = section.getByTestId('host-credential-detail-claude-native');
    await expect(detail).toContainText('gen-3f2a');
    await expect(detail).toContainText('evidence:probe-real-request');
    await expect(detail).toContainText('Renewal item open since');
    await shoot(page, section, 'operator-states');

    await section.getByTestId('host-credential-renew-claude-native').click();
    const dialog = page.getByTestId('claude-sign-in-dialog');
    await expect(dialog).toBeVisible();
    await expect.poll(() => startBody).not.toBeNull();
    expect(startBody).toMatchObject({ mode: 'native' });
    await setTheme(page, 'dark');
    await page.screenshot({ path: join(SHOT_DIR, 'operator-native-renewal-dialog-dark--mocked.png') });
  });

  test('viewer sees health without renewal controls, locators or evidence', async ({ page }) => {
    const section = await openCredentials(page, 'viewer');
    await expect(section.getByTestId('host-credential-status-claude-native')).toContainText('Login invalid');
    await expect(section.locator('[data-testid^="host-credential-renew-"]')).toHaveCount(0);
    await expect(section.locator('[data-testid^="host-credential-actions-"]')).toHaveCount(0);
    await section.getByTestId('host-credential-toggle-claude-native').click();
    const detail = section.getByTestId('host-credential-detail-claude-native');
    await expect(detail).not.toContainText('Locator');
    await expect(detail).not.toContainText('evidence:');
    await shoot(page, section, 'viewer-health-only');
  });

  test('owner sees the administrator runbook for platform-controlled credentials', async ({ page }) => {
    const section = await openCredentials(page, 'owner');
    await expect(section.getByTestId('host-credential-runbook-deploy-key')).toContainText('Open rotation runbook');
    await expect(section.getByTestId('host-credential-admin-deploy-key')).toHaveCount(0);
  });

  test('narrow screen keeps every credential fact readable without horizontal scroll', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 3400 });
    const section = await openCredentials(page, 'operator');
    const card = section.getByTestId('host-credential-claude-token');
    await expect(card).toContainText('Environment file · shadowed');
    await expect(card).toContainText('Coding and Review');
    // Every credential row stays inside the credentials block; facts wrap instead of scrolling.
    const content = section.getByTestId('host-credentials');
    const overflow = await content.evaluate(element => element.scrollWidth - element.clientWidth);
    expect(overflow).toBeLessThanOrEqual(1);
    const facts = await card.getByTestId('host-credential-facts-claude-token').evaluate(list =>
      [...list.children].map(item => item.getBoundingClientRect().right));
    const right = await content.evaluate(element => element.getBoundingClientRect().right);
    for (const edge of facts) expect(edge).toBeLessThanOrEqual(right + 1);
    await shoot(page, section, 'operator-narrow');
  });
});
