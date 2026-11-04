import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

test('consumer policy editor renders in light and dark', async ({ page }) => {
  const results = process.env.JOB_RESULTS_DIR ?? 'test-results';
  mkdirSync(results, { recursive: true });
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    const policy = {
      version: 1,
      consumers: {
        coding: { route: 'qualification', primary: null, fallbacks: 'catalogue' },
        'review-aspect': { route: null, primary: { cli: 'claude', model: 'claude-opus-5', thinking: 'high' },
          fallbacks: [{ cli: 'codex', model: 'gpt-6-sol', thinking: 'high' }] },
      },
      caps: {}, costGuardFactor: 2,
      hysteresis: { returnBelowCapPoints: 10, minDwellMinutes: 30 },
      migration: { inheritedSteps: 36, explicitSteps: 36, projects: 12, applied: true },
    };
    const body = path === '/api/model-selection/policy' ? policy
      : path === '/api/model-selection/effective' ? { routes: [
        { consumer: 'coding', stepId: null,
          configured: { cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high' },
          effective: { cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high' }, rule: 'consumer-default', reason: null },
        { consumer: 'review-aspect', stepId: null,
          configured: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' },
          effective: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' }, rule: 'consumer-default', reason: null },
      ] }
      : path === '/api/v1/studio/auth/status'
        ? { profile: 'local', bootstrapRequired: false, authenticated: true, user: null }
      : path === '/api/v1/studio/runner/status' ? { projects: {} }
      : path === '/api/watcher/status' ? { options: { enabled: false }, contingent: null }
      : path === '/api/watch-paths' || path === '/api/tasks' || path === '/api/tags'
        || path === '/api/cli/contracts'
        || path === '/api/clients' || path === '/api/clients/'
        || path === '/api/v1/workspaces' || path === '/api/v1/projects'
        || path === '/api/v1/management/remote-hosts' || path === '/api/v1/management/links' ? []
      : path.startsWith('/api/v1/studio/board') ? { preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }
      : path === '/api/cli/quota/model-routes' ? { profiles: {}, routes: [], states: {}, callersCannotReroute: [], catalogueVersion: 'mocked' }
      : path === '/api/cli/model-routing/policy' ? { version: 'mocked', economyMode: false, tiers: [], taskTypeDefaults: {} }
      : path === '/api/cli/quota/caps' ? { defaultCapPct: 95, caps: {} }
      : path === '/api/cli/quota' ? { at: new Date().toISOString(), snapshots: [] }
      : path.match(/^\/api\/cli\/[^/]+\/models$/) ? { source: 'mocked', models: [] }
      : path === '/api/cli/usage' ? { sections: [] }
      : path === '/api/usage/cockpit' ? { workspaceId: 'mocked', clis: [], cost: null }
      : null;
    await route.fulfill({ status: body === null ? 503 : 200, contentType: 'application/json', body: JSON.stringify(body ?? {}) });
  });
  await page.goto('/');
  await dismissDevErrorDialog(page);
  await page.getByTestId('studio-titlebar-settings').click();
  await page.getByTestId('workspace-settings-rail-caps').click();
  const editor = page.getByTestId('consumer-policy-editor');
  await expect(editor).toBeVisible();
  await expect(editor.getByTestId('consumer-policy-review-aspect')).toBeVisible();
  await expect(editor.getByTestId('consumer-policy-coding')).toBeVisible();
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(editor).toBeVisible();
    await editor.screenshot({ path: join(results, `consumer-model-policy--mocked-${theme}.png`) });
  }
});
