import { expect, test, type Page, type Route } from '@playwright/test';
import { existsSync, mkdirSync, readFileSync } from 'node:fs';
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
const makeEntry = (id: string, title: string, roleId: string, allowance: number) => ({ id, title,
  aspect: roleId, kind: 'persona', revision: '1', adoptableRevision: null,
  descriptor: { charter: `Review ${roleId} evidence.`, cadence: { report: 'P1D' }, budget: { tokensPerWeek: allowance } },
  activation: { personaId: id, roleId, state: 'active', descriptorRevision: '1',
    allowance: { tokensPerWeek: allowance }, history: [{ at: stamp, action: 'activate', state: 'active', descriptorRevision: '1' }] } });
const baseItem = { personaId: 'mira', roleId: 'security', createdAtUtc: stamp, impact: 4, urgency: 3, confidence: .8, floor: 0 };
const initialItems = () => [
  { ...baseItem, id: 'item-critical', kind: 'finding', title: 'Backup gap', body: 'No scheduled off-host backup was found.',
    importance: 97, state: 'open', impact: 5, urgency: 5, confidence: .94, floor: 95 },
  { ...baseItem, id: 'item-token', kind: 'finding', title: 'Token nearing expiry', body: 'The deploy token expires in 9 days.',
    importance: 54, state: 'open' },
  { ...baseItem, id: 'prop-deferred', kind: 'proposal', title: 'Rotate the deploy token monthly',
    body: 'Add a monthly rotation step to the deploy runbook.', importance: 48, state: 'deferred',
    deferredUntilUtc: new Date(Date.now() + 7 * 86_400_000).toISOString(), dossierKey: 'AGT-3573', dossierSection: 'operations' },
  { ...baseItem, id: 'prop-draft', kind: 'proposal', title: 'Add an off-host backup check',
    body: 'Schedule a daily check that the latest backup left the host.', importance: 62, state: 'draft',
    dossierKey: 'AGT-3573', dossierSection: 'operations' },
];
const issue = {
  id: 'weekly-2026-11-09', workspaceId: 'ws-demo', kind: 'weekly', masthead: 'Studio Weekly',
  coverageLine: '2 personas reported · 2 items', periodStart: '2026-11-09', periodEnd: '2026-11-15',
  strapTotal: 2, lead: { id: 'pit_1', title: 'Backup gap', persona: 'Mira', role: 'Operations warden', importance: 97,
    teaser: null, recommendedOption: null, deadlineUtc: null }, leadParagraph: 'The backup is missing [pit_1].',
  sideStories: [{ id: 'pit_2', title: 'Credential expiry', persona: 'Aster', role: 'Security officer', importance: 95,
    teaser: 'Rotate the credential [pit_2].', recommendedOption: null, deadlineUtc: null }],
  decisionsNeeded: [], operationsWatch: Array.from({ length: 15 }, (_, index) => ({ id: `fact-${index}`, group: index < 8 ? 'Backups' : 'Security posture',
    label: `Fact ${index + 1}`, state: index === 0 ? 'finding' : 'passing', importance: index === 0 ? 97 : 20, itemCount: index === 0 ? 1 : 0 })),
  sections: [{ roleId: 'operations', role: 'Operations', count: 1, stories: [{ id: 'pit_1', title: 'Backup gap', persona: 'Mira', role: 'Operations warden', importance: 97, teaser: null }] },
    { roleId: 'security', role: 'Security', count: 1, stories: [{ id: 'pit_2', title: 'Credential expiry', persona: 'Aster', role: 'Security officer', importance: 95, teaser: null }] }],
  trends: [], corrections: [], colophon: [], editorSkipped: false,
  articles: [{ scope: 'workspace:ws-demo', slug: 'operations-brief', revision: 2, title: 'Operations brief',
    summary: 'Backup status.', persona: 'Mira', personaId: 'mira', role: 'Operations warden' }],
};
const priorIssue = { ...issue, id: 'weekly-2026-11-02', masthead: 'Studio Weekly 44',
  periodStart: '2026-11-02', periodEnd: '2026-11-08' };
const exportFixture = join(resultDir, 'weekly-rendered.html');
const exportedHtml = existsSync(exportFixture) ? readFileSync(exportFixture, 'utf8') :
  '<!doctype html><html><head><meta charset="utf-8"><style data-article-template="v2">:root{color-scheme:light;background:white;color:black}@media(prefers-color-scheme:dark){:root{color-scheme:dark;background:#222;color:white}}@media print{@page{size:A4}body{background:white;color:black}}</style></head><body><h1>Studio Weekly</h1><h2>Backup gap</h2><p>97</p></body></html>';

interface Fixture { releaseStop(): void; releaseDecision(): void; decisions: { itemId: string; outcome: string }[] }

async function setup(page: Page, options: { failStop?: boolean; holdDecision?: boolean } = {}): Promise<Fixture> {
  let items = initialItems();
  let releaseStop: (() => void) | undefined;
  let releaseDecision: (() => void) | undefined;
  const decisions: { itemId: string; outcome: string }[] = [];
  // A connected hub keeps the offline guard from blocking the page's POSTs.
  await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill({ json: {
    negotiateVersion: 1, connectionId: 'team-mock', connectionToken: 'team-mock',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }] } }));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  // Hermetic fallback for the shell's background polls; the page routes below take precedence.
  await page.context().route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname;
    const json = (body: unknown) => route.fulfill({ json: body });
    if (path === '/api/v1/studio/auth/status') return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    if (path === '/api/v1/management/remote-hosts' || path.startsWith('/api/bus/')) return json([]);
    if (/\/api\/(tags|clients|watch-paths)\/?$/.test(path) || /\/api\/(?:tasks|jobs)$/.test(path)) return json([]);
    if (path.startsWith('/api/v1/management/') && !path.endsWith('/credential-inventory')) return json([]);
    if (path.endsWith('/runner/status')) return json({ projects: {} });
    if (path === '/api/usage/cockpit') return json({ workspaceId: 'ws-demo', clis: [], cost: null });
    if (path === '/api/cli/usage') return json({ at: stamp, sessions: [] });
    if (path.endsWith('/cli/quota')) return json({ at: stamp, ttlSeconds: 600, snapshots: [] });
    if (path.startsWith('/api/tasks/archive')) return json({ items: [], total: 0, offset: 0, limit: 0 });
    if (path.endsWith('/quota/admission')) return json({ workspaceId: 'ws-demo', projects: [],
      workspace: { projectId: null, project: null, status: 'ok', message: null, overrideActive: false,
        codingAllowed: true, hostingAllowed: true, limited: false } });
    return json({});
  });
  await page.route(url => ['/api/v1/workspaces', '/api/workspaces'].includes(url.pathname), route => route.fulfill({ json: [workspace] }));
  await page.route(url => url.pathname === '/api/v1/projects', route => route.fulfill({ json: [project] }));
  await page.route('**/api/v1/studio/board*', route => route.fulfill({ json: { backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [], escalated: [], completed: [], archive: [], gitStateAt: null, stale: false } }));
  await page.route('**/api/workspaces/ws-demo/personas**', (route: Route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/issues/weekly-2026-11-09/export')) return route.fulfill({ contentType: 'text/html', body: exportedHtml });
    if (path.endsWith('/issues')) return route.fulfill({ json: [issue, priorIssue] });
    if (path.endsWith('/budget')) return route.fulfill({ json: { poolTokens: 400, usedTokens: 80, startUtc: stamp,
      endUtc: stamp, personas: { mira: { allowanceTokens: 100, usedTokens: 30 }, aster: { allowanceTokens: 150, usedTokens: 50 } } } });
    if (path.endsWith('/items')) return route.fulfill({ json: items });
    if (path.endsWith('/reports')) return route.fulfill({ json: [{ id: 'r1', personaId: 'mira', publishedAtUtc: stamp,
      state: 'published', template: 'security-v1', reason: 'scheduled', openItems: [], sourcesRead: ['audit'], sourcesMissing: [], tokensUsed: 30 }] });
    const decision = path.match(/\/items\/([^/]+)\/decision$/);
    if (decision) {
      const outcome = (route.request().postDataJSON() as { outcome: string }).outcome;
      decisions.push({ itemId: decision[1], outcome });
      const answer = () => {
        items = items.map(item => item.id === decision[1] ? { ...item, state: outcome } : item);
        void route.fulfill({ json: items.find(item => item.id === decision[1]) });
      };
      if (options.holdDecision) { releaseDecision = answer; return; }
      return answer();
    }
    if (path.endsWith('/stop-all')) return new Promise<void>(resolve => {
      releaseStop = () => { void route.fulfill({ status: options.failStop ? 500 : 200, json: options.failStop ? { error: 'test' } : {} }).then(resolve); };
    });
    return route.fulfill({ json: { workspaceId: 'ws-demo', personas: [
      makeEntry('mira', 'Mira', 'security', 100), makeEntry('aster', 'Aster', 'security', 150)] } });
  });
  await page.goto('/#/workspace/team');
  await expect(page.getByTestId('team-card-mira')).toBeVisible();
  return { releaseStop: () => releaseStop?.(), releaseDecision: () => releaseDecision?.(), decisions };
}

const openPersona = async (page: Page) => {
  await page.getByTestId('team-card-mira').getByRole('button', { name: 'Mira' }).click();
  await expect(page.getByTestId('persona-page')).toBeVisible();
};
const openTab = (page: Page, name: string) => page.getByTestId('persona-tabs').getByRole('button', { name }).click();

test('pool totals equal the visible cards; Stop all paints at once and rolls back on failure', async ({ page }) => {
  const fixture = await setup(page, { failStop: true });
  await expect(page.getByTestId('team-used')).toHaveText('80');
  await expect(page.getByTestId('team-allocated')).toHaveText('250');
  await expect(page.getByTestId('team-unallocated')).toHaveText('150');
  await expect(page.getByTestId('team-pool')).toHaveText('400');
  await page.getByTestId('team-stop-all').click();
  await expect(page.getByTestId('team-card-mira')).toContainText('inactive');
  await expect(page.getByTestId('team-card-aster')).toContainText('inactive');
  fixture.releaseStop();
  await expect(page.getByRole('alert')).toContainText('previous state has been restored');
  await expect(page.getByTestId('team-card-mira')).toContainText('active');
});

test('a resolved Critical item loses acute styling immediately, without a reload', async ({ page }) => {
  const fixture = await setup(page, { holdDecision: true });
  await expect(page.getByTestId('team-card-mira')).toHaveClass(/team-card--critical/);
  await openPersona(page);
  await page.getByLabel('State').selectOption('all');
  const critical = page.getByTestId('persona-item-item-critical');
  await expect(critical).toHaveClass(/team-item--critical/);
  const navigations: string[] = [];
  page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations.push(frame.url()); });
  await critical.getByRole('button', { name: 'Resolve' }).click();
  // The decision response is still held: the acute tone must already be gone.
  await expect(critical).not.toHaveClass(/team-item--critical/);
  await expect(critical).toContainText('resolved');
  fixture.releaseDecision();
  await expect.poll(() => fixture.decisions).toEqual([{ itemId: 'item-critical', outcome: 'resolved' }]);
  await page.getByRole('button', { name: '← Team' }).click();
  await expect(page.getByTestId('team-card-mira')).not.toHaveClass(/team-card--critical/);
  await expect(page.getByTestId('team-card-mira')).toContainText('62');
  expect(navigations.filter(url => !url.includes('#/workspace/team'))).toEqual([]);
});

test('a deferred proposal stays decidable and decisions post the scout outcome', async ({ page }) => {
  const fixture = await setup(page);
  await openPersona(page);
  await openTab(page, 'Proposals');
  await expect(page.getByTestId('persona-tabs').getByRole('button', { name: 'Proposals' })).toHaveAttribute('aria-pressed', 'true');
  const deferred = page.getByRole('article').filter({ hasText: 'Rotate the deploy token monthly' });
  await expect(deferred).toContainText('deferred until');
  await expect(deferred.getByRole('button', { name: 'Accept' })).toBeEnabled();
  await deferred.getByRole('button', { name: 'Accept' }).click();
  await expect.poll(() => fixture.decisions).toEqual([{ itemId: 'prop-deferred', outcome: 'accepted' }]);
  await expect(deferred.getByRole('button', { name: 'Accept' })).toHaveCount(0);
});

test('Ask opens the in-app chat for the persona', async ({ page }) => {
  await setup(page);
  await openPersona(page);
  await page.getByTestId('persona-ask').click();
  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
});

test('team and persona pages render in light and dark', async ({ page }) => {
  mkdirSync(resultDir, { recursive: true });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await setup(page);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(page.getByTestId('team-page')).toBeVisible();
    await page.screenshot({ path: join(resultDir, `team-${theme}.png`), fullPage: true });
    await openPersona(page);
    await page.screenshot({ path: join(resultDir, `persona-feed-${theme}.png`), fullPage: true });
    await openTab(page, 'Proposals');
    await expect(page.getByTestId('persona-tabs').getByRole('button', { name: 'Proposals' })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('button', { name: 'Accept' }).first()).toBeVisible();
    await page.screenshot({ path: join(resultDir, `persona-proposals-${theme}.png`), fullPage: true });
    await page.getByRole('button', { name: '← Team' }).click();
  }
});

test('weekly issue renders in both themes and its HTML export prints to PDF', async ({ page }) => {
  mkdirSync(resultDir, { recursive: true });
  await setup(page);
  await expect(page.getByRole('heading', { name: 'Latest issues' })).toBeVisible();
  await page.getByRole('button', { name: /Studio Weekly 44/ }).click();
  await expect(page.getByTestId('weekly-issue')).toContainText('Studio Weekly 44');
  await page.getByRole('button', { name: 'weekly-2026-11-09' }).click();
  await expect(page.getByTestId('weekly-issue')).toContainText('Studio Weekly');
  await page.getByRole('button', { name: '← Team' }).click();
  await page.getByRole('button', { name: 'View magazine' }).click();
  await expect(page.getByTestId('team-magazine-page')).toBeVisible();
  const viewer = page.getByTestId('weekly-issue');
  await expect(viewer.getByRole('heading', { name: 'Studio Weekly' })).toBeVisible();
  await expect(viewer.getByRole('heading', { name: 'Backup gap' }).first()).toBeVisible();
  await expect(viewer.getByRole('heading', { name: 'Operations brief' })).toBeVisible();
  await expect(viewer).toContainText('Mira · Operations warden · revision 2');
  await expect(viewer.getByTestId('weekly-watch-row')).toHaveCount(15);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: join(resultDir, `weekly-view-${theme}.png`), fullPage: true });
  }
  const download = page.waitForEvent('download');
  await page.getByTestId('weekly-export').click();
  const file = join(resultDir, 'weekly-2026-11-09.html');
  await (await download).saveAs(file);
  const html = readFileSync(file, 'utf8');
  expect(html).toContain('data-article-template="v2"');
  expect(html).toContain('@media print');
  expect(html).toContain('prefers-color-scheme:dark');
  await page.setContent(html);
  for (const theme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: theme });
    await page.screenshot({ path: join(resultDir, `weekly-export-${theme}.png`), fullPage: true });
  }
  await page.pdf({ path: join(resultDir, 'weekly-export.pdf'), format: 'A4', printBackground: true });
});
