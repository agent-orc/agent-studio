import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';
const resultDir = process.env.JOB_RESULTS_DIR || 'test-results/persona-team';
const project = { sourceType: 'local-folder', id: 'PROJ-002', displayName: 'Demo Project', shortCode: 'DEMO',
  workspaceId: 'ws-demo', color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
  storageLocation: '/tmp/demo-project', repositoryPath: null, rootPath: null, repositoryUrl: null,
  urls: [], archived: false, createdAt: '2026-11-10T00:00:00Z' };
const workspace = { id: 'ws-demo', displayName: 'Demo workspace', sortOrder: 0, isDefault: true,
  color: null, createdAt: '2026-11-10T00:00:00Z', projects: [project] };
const stamp = new Date().toISOString();
const makeEntry = (id: string, roleId: string, allowance: number) => ({ id, title: id === 'mira' ? 'Mira' : 'Aster',
  aspect: roleId, kind: 'persona', revision: '1', adoptableRevision: null,
  descriptor: { charter: `Review ${roleId} evidence.`, cadence: { report: 'P1D' }, budget: { tokensPerWeek: allowance } },
  activation: { personaId: id, roleId, state: 'active', descriptorRevision: '1',
    allowance: { tokensPerWeek: allowance }, history: [{ at: stamp, action: 'activate', state: 'active', descriptorRevision: '1' }] } });
const finding = (state: string) => ({ id: 'item-1', personaId: 'mira', roleId: 'security', kind: 'finding',
  title: 'Backup gap', body: 'No scheduled off-host backup was found.', importance: 97, state,
  createdAtUtc: stamp, impact: 5, urgency: 5, confidence: .94, floor: 95 });
async function setup(page: import('@playwright/test').Page, options: { failStop?: boolean } = {}) {
  let itemState = 'open'; let resolveStop: (() => void) | undefined;
  await page.route('**/api/v1/workspaces*', route => route.fulfill({ json: [workspace] }));
  await page.route('**/api/v1/projects*', route => route.fulfill({ json: [project] }));
  await page.route('**/api/v1/studio/board*', route => route.fulfill({ json: { backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [], escalated: [], completed: [], archive: [], gitStateAt: null, stale: false } }));
  await page.route('**/api/workspaces/ws-demo/personas**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/budget')) return route.fulfill({ json: { poolTokens: 400, usedTokens: 80, startUtc: stamp,
      endUtc: stamp, personas: { mira: { allowanceTokens: 100, usedTokens: 30 }, aster: { allowanceTokens: 150, usedTokens: 50 } } } });
    if (path.endsWith('/items')) return route.fulfill({ json: [finding(itemState)] });
    if (path.endsWith('/reports')) return route.fulfill({ json: [{ id: 'r1', personaId: 'mira', publishedAtUtc: stamp,
      state: 'published', template: 'security-v1', reason: '', openItems: [], sourcesRead: ['audit'], sourcesMissing: [], tokensUsed: 30 }] });
    if (path.endsWith('/stop-all')) return new Promise<void>(resolve => {
      resolveStop = () => { void route.fulfill({ status: options.failStop ? 500 : 200, json: options.failStop ? { error: 'test' } : {} }).then(resolve); };
    });
    return route.fulfill({ json: { workspaceId: 'ws-demo', personas: [makeEntry('mira', 'security', 100), makeEntry('aster', 'security', 150)] } });
  });
  await page.goto('/#/workspace/team');
  await expect(page.getByTestId('team-card-mira')).toBeVisible();
  return { resolve: () => resolveStop?.(), setItemState: (state: string) => { itemState = state; } };
}
test('pool and acute state reconcile; stop paints then rolls back', async ({ page }) => {
  const fixture = await setup(page, { failStop: true });
  await expect(page.getByTestId('team-used')).toHaveText('80');
  await expect(page.getByTestId('team-allocated')).toHaveText('250');
  await expect(page.getByTestId('team-unallocated')).toHaveText('150');
  await expect(page.getByTestId('team-card-mira')).toHaveClass(/team-card--critical/);
  fixture.setItemState('resolved');
  await page.reload();
  await expect(page.getByTestId('team-card-mira')).not.toHaveClass(/team-card--critical/);
  await page.getByTestId('team-stop-all').click();
  await expect(page.getByTestId('team-card-mira')).toContainText('inactive');
  fixture.resolve();
  await expect(page.getByRole('alert')).toContainText('previous state has been restored');
  await expect(page.getByTestId('team-card-mira')).toContainText('active');
});
test('team and persona pages render in light and dark', async ({ page }) => {
  mkdirSync(resultDir, { recursive: true });
  await setup(page);
  await page.setViewportSize({ width: 1280, height: 850 });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(page.getByTestId('team-page')).toBeVisible();
    await page.screenshot({ path: join(resultDir, `team-${theme}.png`), fullPage: true });
    await page.getByTestId('team-card-mira').getByRole('button', { name: 'Mira' }).click();
    await expect(page.getByTestId('persona-page')).toBeVisible();
    await page.screenshot({ path: join(resultDir, `persona-${theme}.png`), fullPage: true });
    await page.getByRole('button', { name: '← Team' }).click();
  }
});
