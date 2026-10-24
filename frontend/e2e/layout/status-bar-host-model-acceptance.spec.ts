import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const results = process.env.JOB_RESULTS_DIR ?? 'test-results';

test('networked quota shows accepted models for each host in both themes', async ({ page }) => {
  mkdirSync(results, { recursive: true });
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ json: {
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [],
      disabled: false, mustChangePassword: false },
  } }));
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({ json: [{
    runnerId: 'agent-runner-01', name: 'agent-runner-01', hostId: 'host-01',
    instanceId: 'coding-01', runnerVersion: '1', protocolVersion: 2,
    status: 'active', registeredAt: new Date().toISOString(), lastSeenAt: new Date().toISOString(),
    hostAdmission: { hostId: 'host-01', admissionState: 'open' },
    capabilities: [
      capability('provider-auth:codex', 'provider-auth', 'ready'),
      capability('model-acceptance:codex:gpt-6-luna', 'model-acceptance', 'ready'),
      capability('model-acceptance:codex:gpt-6-sol', 'model-acceptance', 'unavailable'),
    ],
  }] }));
  await page.route('**/api/v1/management/links', route => route.fulfill({ json: [] }));
  await page.setViewportSize({ width: 1600, height: 900 });
  await page.goto('/');
  const card = page.getByTestId('hquota-card-codex');
  await expect(card).toContainText('gpt-6-luna');
  await expect(card).not.toContainText('gpt-6-sol');
  await expect(card).toHaveAttribute('aria-label', /agent-runner-01 ok; accepted models: gpt-6-luna/);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await card.screenshot({ path: join(results, `host-model-acceptance-${theme}.png`) });
  }
});

function capability(key: string, category: string, advertisedStatus: string) {
  return {
    key, category, advertisedStatus, healthState: 'healthy', reason: null,
    advertisedAt: new Date().toISOString(), freshUntil: new Date(Date.now() + 120_000).toISOString(),
    isFresh: true, firstFailureAt: null, lastFailureAt: null, cooldownUntil: null,
    canaryClaimId: null, consecutiveFailures: 0, version: null, identity: null,
    detail: 'Probe completed', affectedClaims: [], recoveryHistory: [],
  };
}
