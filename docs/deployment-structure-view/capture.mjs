// Evidence captures for docs/deployment-structure-view (AGT-3349).
// Serves the prebuilt frontend of this checkout and answers every API call from
// the role-named fixture below; no backend, workspace or provider is contacted.
// The fixture reports the product's seeded runner id with a role-named host id,
// so the seeded host joins the role-named machine; only role names are visible.
// Prerequisite: npm --prefix frontend run build
// Run from the repository root: node docs/deployment-structure-view/capture.mjs
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { spawn, execFileSync } from 'node:child_process';
import { writeFileSync, existsSync, readFileSync } from 'node:fs';
import net from 'node:net';

const dir = dirname(fileURLToPath(import.meta.url));
const repo = resolve(dir, '../..');
const require = createRequire(resolve(repo, 'frontend/package.json'));
const { chromium } = require('playwright-core');
const dist = resolve(repo, 'frontend/dist/frontend/browser');
if (!existsSync(resolve(dist, 'index.html'))) throw new Error('Build the frontend first.');

const port = await new Promise(ok => { const s = net.createServer().listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => ok(p)); }); });
const server = spawn(process.execPath, [resolve(repo, 'frontend/scripts/serve-prebuilt-frontend.mjs'), dist, String(port), 'http://127.0.0.1:9'], { stdio: 'ignore' });
const base = `http://127.0.0.1:${port}`;
for (let i = 0; i < 50; i++) { try { await fetch(base); break; } catch { await new Promise(r => setTimeout(r, 200)); } }

const NOW = '2026-11-01T12:00:00.000Z';
// The product seeds one remote runner (remote-hosts.seed.ts). Read its id at run
// time so the fixture can report it under a role name without naming it here.
const seed = readFileSync(resolve(repo, 'frontend/src/app/features/remote-hosts/services/remote-hosts.seed.ts'), 'utf8');
const SEEDED_RUNNER = [...seed.matchAll(/id: '([^']+)'/g)].map(m => m[1]).find(id => id !== 'local');
if (!SEEDED_RUNNER) throw new Error('Seeded runner id not found');
const projects = [
  { id: 'agt', displayName: 'Agent Studio', shortCode: 'AGT', workspaceId: 'main', storageLocation: 'C:/fixture/agent-studio' },
  { id: 'web', displayName: 'Website', shortCode: 'WEB', workspaceId: 'main', storageLocation: 'C:/fixture/website' },
  { id: 'lib', displayName: 'Chat library', shortCode: 'LIB', workspaceId: 'main', storageLocation: 'C:/fixture/chat-library' },
];
const release = (projectId, version) => ({ id: `${projectId}-${version}`, projectId, version, commitSha: 'a'.repeat(40),
  artifactDigests: [], processVersion: '1', variablesVersion: '1', cardKeys: [], createdAtUtc: '2026-10-31T10:00:00Z', channel: 'default' });
const status = (projectId, kind, version) => ({
  environment: { id: kind, projectId, name: kind, order: kind === 'staging' ? 0 : 1, kind },
  currentRelease: release(projectId, version),
  currentDeployment: { id: `${projectId}-${kind}`, releaseId: `${projectId}-${version}`, environmentId: kind, state: 'succeeded',
    startedAtUtc: '2026-10-31T11:00:00Z', finishedAtUtc: '2026-10-31T11:20:00Z', approvedBy: null, steps: [], tenant: null },
  newerRelease: null, cardsSinceLastDeploy: [], health: 'healthy', tenant: null,
});
const environment = {
  controlHost: 'Studio machine', installationId: 'studio', mode: 'reverse-ssh-transition',
  routes: [{ runnerId: SEEDED_RUNNER, route: 'reverse-ssh' }],
  machines: [
    { id: 'studio-machine', name: 'Studio machine', components: [
      { id: 'studio', name: 'Studio backend', role: 'control', kind: 'service' },
      { id: 'update', name: 'Update service', role: 'control', kind: 'service' } ] },
    { id: 'linux-runner-host', name: 'Linux runner host', components: [
      { id: 'coding', name: 'Coding host', role: 'coding', kind: 'service' },
      { id: 'review', name: 'Review host', role: 'review', kind: 'service' },
      { id: 'engine', name: 'Orchestrator Engine', role: 'control', kind: 'container' },
      { id: 'integrator', name: 'Promotion checkout', role: 'integration', kind: 'service' } ] },
    { id: 'homelab-vm', name: 'Home-lab VM', components: [1, 2, 3].map(i => ({ id: `review-${i}`, name: `Review container ${i}`, role: 'review', kind: 'container' }))
      .concat([{ id: 'gate', name: 'Build and test gate', role: 'gate', kind: 'container' }]) },
  ],
  gateHost: 'Home-lab VM', gateSlots: { busy: 1, total: 3 },
  taskServer: { version: '2026.10.31.2', commit: null },
  engine: { state: 'ready', lastContact: NOW, engineId: 'engine-1' },
};

async function routeAll(context) {
  await context.route('**/api/**', route => {
    const url = new URL(route.request().url());
    const p = url.pathname;
    const json = body => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (p.endsWith('/auth/status')) return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    if (p === '/api/v1/management/environment') return json(environment);
    if (p === '/api/v1/management/credential-inventory') return json({ generatedAt: NOW, items: [] });
    if (p === '/api/watch-paths') return json(projects.map(x => ({ name: x.displayName, path: x.storageLocation })));
    if (p === '/api/v1/projects') return json(projects);
    if (p.includes('/quota/admission')) return json({ workspaceId: 'main', workspace: { projectId: null, project: null, status: 'ok', message: null,
      overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false }, projects: [] });
    if (p === '/api/v1/workspaces' || p === '/api/workspaces') return json([{ id: 'main', displayName: 'Main', sortOrder: 0, isDefault: true, color: null,
      createdAt: '2026-10-01T00:00:00Z', projects }]);
    if (p.startsWith('/api/v1/studio/board')) return json({ backlog: [], preparation: [], orchestratorPrep: [], failedPickup: [], codeNotComplete: [],
      autoReview: [], review: [], completed: [], archive: [], ready: [], progress: [], humanReview: [], escalated: [], gitStateAt: null, stale: false });
    if (p === '/api/tasks/archive') return json({ items: [], total: 0, offset: 0, limit: 50 });
    if (/\/api\/projects\/[^/]+\/workbenches/.test(p)) return json({ projectName: 'x', includesHistory: true, count: 0, items: [] });
    if (p === '/api/workbenches') return json({ projectName: null, count: 0, currentCount: 0, historyCount: 0, items: [] });
    if (p === '/api/v1/studio/runner/status') return json({ projects: {} });
    if (p === '/api/environment') return json({ isDev: false, devTools: {} });
    if (p === '/api/usage/cockpit') return json({ workspaceId: 'main', clis: [], cost: null });
    if (p === '/api/cli/usage') return json({ at: NOW, sessions: [] });
    if (p === '/api/cli/quota') return json({ at: NOW, ttlSeconds: 600, ttlMs: 600000, snapshots: [] });
    if (p === '/api/v1/management/remote-hosts') return json([{ runnerId: SEEDED_RUNNER, name: 'Coding host', hostId: 'Linux runner host',
      instanceId: 'i-1', runnerVersion: '2026.10.31.2', protocolVersion: 1, status: 'online', registeredAt: NOW, lastSeenAt: NOW,
      hostAdmission: { admissionState: 'open' }, capabilities: [], telemetry: null, runtimeCapacity: null, effectiveMaxParallelism: 2,
      release: null, activeGateCount: 0 }]);
    if (p === '/api/system/execution-infrastructure') return json({ gate: { host: 'Home-lab VM', transport: 'ssh', busy: 1, total: 3, reachability: 'reachable',
      lastReachedAt: NOW, lastFailureAt: null }, taskServer: 'local', engine: { state: 'ready', required: true, lastContact: NOW, lastEngineId: 'engine-1' }, workstation: 'Studio machine' });
    if (p === '/api/crash-recovery/pending') return json({ pending: [] });
    if (p === '/api/runner/queue-starvation') return json({ active: false, waitingTaskCount: 0, availableSlots: 0, thresholdMinutes: 30, observedAt: NOW, oldestEnteredLaneAt: null, items: [] });
    if (p.endsWith('/deploy/delivery-lanes')) return json({ projectId: decodeURIComponent(p.split('/')[3]), lanes: [
      { environmentId: null, name: 'Waiting for release', order: -1, cards: [] },
      { environmentId: 'staging', name: 'Stage', order: 0, cards: [] },
      { environmentId: 'production', name: 'Production', order: 1, cards: [] } ] });
    if (p === '/api/deploy/matrix') return json({ environmentKinds: ['staging', 'production'], projects: [
      { projectId: 'Website', error: null, statuses: ['staging', 'production'].map(k => status('Website', k, '2026.10.31.1')), tenantStatuses: [] } ] });
    if (process.env.CAPTURE_DEBUG) console.error('fallback', p);
    return json([]);
  });
}

const report = { capturedAt: new Date().toISOString(), commit: execFileSync('git', ['-C', repo, 'rev-parse', 'HEAD']).toString().trim(),
  provenance: 'Prebuilt frontend of this commit; every API response from the role-named fixture in capture.mjs; no backend.', frames: [] };
const browser = await chromium.launch({ headless: true, ...(process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.ATP_PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}) });
try {
  for (const theme of ['light', 'dark']) {
    const context = await browser.newContext({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1, reducedMotion: 'reduce', colorScheme: theme, timezoneId: 'Europe/Berlin' });
    await context.addInitScript(t => { try { localStorage.setItem('atp.studio.theme', t); } catch {} }, theme);
    await routeAll(context);
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.clock.setFixedTime(NOW);
    const settle = async () => { await page.evaluate(async () => { await document.fonts.ready; await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))); }); };
    const stamp = async () => page.evaluate(t => { document.documentElement.dataset.studioTheme = t; }, theme);

    await page.goto(`${base}/#/workspace/settings/environment`, { waitUntil: 'load' });
    await stamp();
    const toggle = page.getByTestId('environment-map-toggle');
    await toggle.waitFor({ state: 'visible', timeout: 30000 });
    const dlg = page.getByTestId('error-dialog-overlay');
    if (await dlg.isVisible().catch(() => false)) throw new Error('Error dialog: ' + (await dlg.innerText()));
    if (await toggle.getAttribute('aria-pressed') !== 'true') await toggle.click();
    const map = page.getByTestId('environment-map');
    await map.waitFor({ state: 'visible', timeout: 15000 });
    await page.getByTestId('environment-machine').filter({ hasText: 'Home-lab VM' }).first().waitFor({ timeout: 10000 });
    await settle();
    const cardsName = `environment-map-components-below-${theme}--mocked.png`;
    await map.scrollIntoViewIfNeeded();
    const box = await map.boundingBox();
    await page.screenshot({ path: resolve(dir, 'assets', cardsName), animations: 'disabled',
      clip: { x: box.x, y: box.y, width: box.width, height: 600 } });
    report.frames.push(cardsName);

    await page.evaluate(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1, tabs: [{ kind: 'deployment-dashboard' }], activeKey: 'deployment-dashboard' })));
    await page.goto(`${base}/`, { waitUntil: 'load' });
    await stamp();
    const dashboard = page.getByTestId('deployment-dashboard');
    await dashboard.waitFor({ state: 'visible', timeout: 30000 }).catch(async e => { await page.screenshot({ path: '/tmp/agt3349-debug.png' }); throw e; });
    await dashboard.getByRole('row').nth(1).waitFor({ timeout: 10000 });
    await settle();
    const dashName = `deployment-dashboard-matrix-${theme}--mocked.png`;
    const top = await dashboard.boundingBox();
    const table = await dashboard.locator('table').boundingBox();
    await page.screenshot({ path: resolve(dir, 'assets', dashName), animations: 'disabled',
      clip: { x: top.x, y: top.y, width: top.width, height: table.y + table.height - top.y + 16 } });
    report.frames.push(dashName);

    if (errors.length) throw new Error(errors.join('\n'));
    await context.close();
  }
  writeFileSync(resolve(dir, 'assets/capture-manifest.json'), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify(report, null, 2));
} finally { await browser.close(); server.kill(); }
