import { expect as playwrightExpect, test, type Locator, type Page, type Route } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { contrastRatio } from '../helpers/contrast';
import { readinessItem } from '../../src/app/features/acceptance-readiness/testing/acceptance-readiness.fixtures';
import { securityPostureFixture } from '../../src/app/features/project-detail/testing/security-posture-fixtures';

// The production shell loads its settings and board chunks on demand.
const expect = playwrightExpect.configure({ timeout: 45_000 });
const PROJECT = 'Card flow fixture';
const WATCH = '/fixtures/card-flow';
const NOW = '2026-11-26T09:00:00Z';
const RESULTS = process.env.JOB_RESULTS_DIR ?? 'test-results/preview-token-sets';
const OWNER = 'studio-release-automation-service-account-with-a-deliberately-long-name';
const SA_ID = 'service-account:sa_preview';
const make = (key: string, state: string, extra = {}) => ({
  id: key.toLowerCase(), key, displayKey: key, taskKey: `${WATCH}::${key.toLowerCase()}`,
  title: `Delivery for ${key}`, state, projectName: PROJECT, watchPath: WATCH,
  folderPath: `${WATCH}/${state}/${key.toLowerCase()}`, agent: 'codex', cliType: 'codex',
  createdAt: NOW, lastActivity: NOW, enteredLaneAt: NOW, commits: [], tags: [], ...extra,
});
const json = (route: Route, body: unknown) => route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });

async function installFixture(page: Page, synthesis = false) {
  const ready = synthesis ? [
    make('AGT-4141', '2-ready', { title: 'Preserve claims on reconnect', ownerClientId: SA_ID }),
    make('AGT-4148', '2-ready', { title: 'Compare the workbench directions' }),
  ] : [make('AGT-4129', '2-ready', { ownerClientId: SA_ID })];
  const progress = synthesis ? [make('AGT-4138', '3-in-progress', { title: 'Separate host slots from project quota' })] : [];
  const humanReview = synthesis ? [make('AGT-4142', '5-human-review', { title: 'Verify the integration evidence' })] : [];
  const cards = [...ready, ...progress, ...humanReview];
  page.setDefaultTimeout(45_000);
  page.setDefaultNavigationTimeout(60_000);
  await page.clock.setFixedTime(new Date(NOW));
  await page.addInitScript(synthesis => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(synthesis && !location.hash
    ? { v: 1, tabs: [], activeKey: null } : {
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'workspace-settings' }], activeKey: 'board:__all__',
  })), synthesis);
  await page.route('**/api/**', route => json(route, []));
  // The static prebuilt proxy does not forward the application's build-health probe.
  await page.route('**/healthz', route => route.fulfill({ contentType: 'text/plain', body: 'ok' }));
  await page.route('**/update/status', route => json(route, { phase: 'idle', isRunning: false, behindBy: 0 }));
  await page.route(/\/api\/(?:v1\/studio\/auth\/status|auth\/status)(?:\?|$)/, route => json(route, {
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'preview-owner', username: 'owner', displayName: 'Studio owner', role: 'owner', projects: [], disabled: false, hasFactor: true },
  }));
  await page.route(/\/api\/v1\/projects(\?|$)/, route => json(route, [{ id: PROJECT, displayName: PROJECT, shortCode: 'AGT', storageLocation: WATCH, archived: false }]));
  const project = { id: PROJECT, displayName: PROJECT, shortCode: 'AGT', storageLocation: WATCH,
    sourceType: 'local-folder', workspaceId: 'preview-workspace', sortOrder: 0, archived: false,
    repositoryPath: WATCH, rootPath: WATCH, urls: [], ownershipMappings: [] };
  await page.route('**/api/v1/workspaces**', route => json(route, [{ id: 'preview-workspace', displayName: 'Studio', projects: [project] }]));
  await page.route(/\/api\/projects(\?|$)/, route => json(route, [project]));
  await page.route(/\/api\/workbenches(\?|$)/, route => json(route, { projectName: null, count: 0, currentCount: 0, historyCount: 0, items: [] }));
  await page.route('**/api/projects/*/workbenches**', route => json(route, { projectName: PROJECT, includesHistory: true, count: 0, items: [] }));
  await page.route('**/api/projects/*/environments', route => json(route, { project: PROJECT, environments: [], promotions: [], failureCards: [] }));
  const integrity = { state: 'not-run', checkedAt: null, verifiedThrough: 0, message: null, alert: false };
  await page.route('**/api/auth/audit/backups**', route => json(route, { entries: [], integrity }));
  await page.route('**/api/auth/audit/operator-activity**', route => json(route, {
    operatorAgents: [], entries: [], integrity,
    forwarding: { sinkKind: 'none', configured: false, headSequence: 0, forwardedSequence: 0, pending: 0,
      lastForwardedAt: null, oldestPendingAt: null, lastError: null, alert: false },
  }));
  await page.route('**/api/auth/iam/operator-catalogue', route => json(route, {
    operations: [], defaultGrantDays: 1, maxGrantDays: 7, defaultRatePerHour: 1, maxRatePerHour: 10,
  }));
  await page.route('**/api/auth/iam/operator-kill-switch', route => json(route, {
    kills: [], drills: [], nextDrillAt: null, drillIntervalHours: 24, operationsServerConfigured: false, outOfBandRequestPending: false,
  }));
  await page.route('**/api/workspaces/*/quota/admission', route => json(route, {
    workspaceId: 'preview-workspace', projects: [], cardRefusals: {},
    workspace: { projectId: null, project: null, status: 'ok', message: null, overrideActive: false,
      codingAllowed: true, hostingAllowed: true, limited: false },
  }));
  await page.route('**/api/v1/management/credential-inventory**', route => json(route, { generatedAt: NOW, items: [] }));
  await page.route('**/api/tasks/*/runs**', route => json(route, {
    runCount: 0, firstStartedAt: null, lastActivityAt: null, hasActiveRun: false,
    runs: [], promptEntries: [], refinements: [], runnerEvents: [],
  }));
  await page.route('**/api/tasks/*/session-events**', route => json(route, { events: [], sessionChain: [] }));
  await page.route('**/api/tasks/*/plan**', route => json(route, {
    hasPlan: false, source: null, snapshotCount: 0, activeItemId: null, items: [], unassignedSubActions: [],
  }));
  await page.route('**/api/tasks/*/pipeline**', route => json(route, {
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null, executions: [], config: {}, cost: null,
  }));
  await page.route('**/code-review/list**', route => json(route, { entries: [] }));
  await page.route('**/api/tasks/acceptance-readiness', route => json(route, {
    checkedAtUtc: NOW, counts: { ready: humanReview.length },
    items: humanReview.map(card => readinessItem(card.id, 'ready', {
      taskKey: card.taskKey, key: card.key, title: card.title, projectName: PROJECT,
    })),
  }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0 }));
  await page.route(/\/api\/tasks(\?|$)/, route => json(route, cards));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready, progress, failedPickup: [],
    codeNotComplete: [], autoReview: [], review: [], humanReview, escalated: [], completed: [], archive: [],
  }));
  await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/agt-[0-9]+(?:\?|$)/, route => {
    const id = /\/tasks\/(agt-[0-9]+)/.exec(route.request().url())![1];
    return json(route, { info: cards.find(card => card.id === id), promptMarkdown: 'Fixture brief.',
      statusMarkdown: '', log: [], promptHistory: [], titleHistory: [], reviewEvidence: [], summaryState: null });
  });
  await page.route(/\/api\/tasks\/agt-[0-9]+\/(core|details\/[^?]+)(?:\?|$)/i, route => {
    const match = /\/tasks\/(agt-[0-9]+)\/(core|details\/([^?]+))/i.exec(route.request().url())!;
    const card = cards.find(item => item.id === match[1].toLowerCase())!;
    if (match[2] === 'core') return json(route, {
      ...card, projectId: PROJECT, kind: 'task', taskType: 'chore', lane: card.state, mode: 'coding',
      state: 'ready', order: 1, released: false, coreVersion: '1', runtimeVersion: '1',
      pins: { modelExplicit: false, thinkingLevelExplicit: false, allowWebAccess: false, noBranchExpected: false },
      actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
      blocking: { dependencyBlocked: false, dependencyState: 'ready', dependencies: [], dependsOn: [], blockedBy: [] },
      runtime: { location: 'none', leaseState: 'none' },
      statusSummary: { state: 'missing', originalBytes: 0 },
      prompt: { state: 'ready', text: 'Fixture brief.', originalBytes: 14 },
      timeline: { state: 'missing', events: [], originalBytes: 0 },
    });
    const resource = match[3];
    const data = resource === 'overview' ? card : resource === 'documents'
      ? { name: new URL(route.request().url()).searchParams.get('name'), markdown: 'Fixture brief.', summaryState: null }
      : resource === 'review' ? { reviewProjection: null, evidence: [] } : {};
    return json(route, { id: card.id, taskKey: card.taskKey, projectId: PROJECT, attemptId: null,
      coreVersion: '1', resource, version: '1', computedAt: NOW, state: 'ready', data, reason: null });
  });
  await page.route('**/api/watch-paths**', route => json(route, [{ name: PROJECT, path: WATCH, rootPath: WATCH, repositoryPath: WATCH }]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, route => json(route, {}));
  await page.route('**/api/cli/usage**', route => json(route, { at: NOW, sessions: [] }));
  await page.route('**/api/cli/quota**', route => json(route, { at: NOW, snapshots: [] }));
  await page.route(/\/api\/usage\/cockpit(\?|$)/, route => json(route, { workspaceId: 'fixture', clis: [], cost: null }));
  await page.route('**/api/v1/studio/runner/status**', route => json(route, { projects: {} }));
  await page.route('**/api/auth/iam', route => json(route, {
    roles: [], permissions: [], iamVersion: 1, groups: [], bindings: [], invitations: [],
    users: [{ id: 'preview-owner', username: 'owner', displayName: 'Studio owner', role: 'owner', projects: [], disabled: false, hasFactor: true }],
    serviceAccounts: [{ id: 'sa_preview', name: OWNER, description: 'Delivery automation', createdAt: NOW, tokens: [] }],
  }));
  await page.route('**/api/clients**', route => json(route, [{ id: SA_ID, displayName: OWNER, kind: 'service-account' }]));
  await page.route(url => url.pathname === '/api/v1/operations/structure', route => json(route, { nodes: [], links: [] }));
  await page.route('**/api/v1/operations/structure/library**', route => json(route, { registries: [], consumers: [] }));
  await page.route('**/api/diagnostics/api-stalls**', route => json(route, {
    status: 'ok', intervalMs: 1000, maxDelayMs: 0, alarmThresholdMs: 1000, alarmWindowMinutes: 5, recent: [],
  }));
  await page.route('**/api/security/posture', route => json(route, securityPostureFixture()));
  await page.route('**/api/operator/attention', route => json(route, { count: 1, items: [{
    id: 'human-review|AGT-4129', type: 'human-review.ready', project: PROJECT, cardKey: 'AGT-4129',
    cards: ['AGT-4129'], reason: 'Choose the next Studio appearance.', observedAt: NOW, suggestedOperations: ['card.inspect'], evidence: [],
  }] }));
  await page.route('**/api/projects/*/deploy/operations**', route => json(route, {
    project: PROJECT, since: NOW, until: NOW, environments: [], telemetryAvailable: false,
    dora: { successfulProductionDeploys: 0, deploysPerWeek: null, medianCardToProductionHours: null, cardsMeasured: 0,
      changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 },
  }));
  await page.route('**/hubs/v1/studio/negotiate**', route => json(route, {
    negotiateVersion: 1, connectionId: 'preview', connectionToken: 'preview',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  }));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => socket.onMessage(message => {
    for (const frame of message.toString().split('\u001e').filter(Boolean)) {
      const incoming = JSON.parse(frame);
      if (incoming.protocol) socket.send('{}\u001e');
      else if (incoming.type === 1 && incoming.invocationId) {
        socket.send(JSON.stringify({ type: 3, invocationId: incoming.invocationId, result: null }) + '\u001e');
      } else if (incoming.type === 6) socket.send('{"type":6}\u001e');
    }
  }));
}


async function capture(page: Page, name: string) {
  mkdirSync(RESULTS, { recursive: true });
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.evaluate(() => { if (document.activeElement instanceof HTMLElement) document.activeElement.blur(); });
  await page.mouse.move(0, 0);
  await page.screenshot({ path: join(RESULTS, `${name}--mocked.png`), animations: 'disabled' });
}

async function auditContrast(page: Page, name: string) {
  mkdirSync(RESULTS, { recursive: true });
  const surfaces = ['bg-surface', 'bg-editor', 'bg-sidebar', 'bg-elevated', 'bg-hover', 'bg-selected',
    'surface-muted', 'card-bg', 'nav-active-bg', 'bg-running', 'card-attention-bg'];
  const inks = ['fg', 'fg-strong', 'fg-dim', 'fg-muted', 'accent-strong', 'status-success', 'status-warning',
    'status-danger', 'status-running', 'status-info', 'accent-4-strong'];
  const pairs = surfaces.flatMap(bg => inks.map(fg => [`--studio-${fg}`, `--studio-${bg}`]));
  pairs.push(['--studio-on-accent', '--studio-accent'], ['--studio-tooltip-fg', '--studio-tooltip-bg']);
  for (const family of ['codex', 'claude', 'gemini']) {
    for (const bg of surfaces) pairs.push([`--model-family-${family}`, `--studio-${bg}`]);
  }
  const sampled = await page.evaluate(pairs => {
    const probe = document.createElement('span');
    document.body.append(probe);
    const rows = pairs.map(([fg, bg]) => {
      probe.style.color = `var(${fg})`;
      probe.style.backgroundColor = `var(${bg})`;
      const computed = getComputedStyle(probe);
      // Canvas normalizes color-mix serialization to rgb for the WCAG helper.
      const canvas = document.createElement('canvas');
      const context = canvas.getContext('2d')!;
      const rgb = (value: string) => {
        context.fillStyle = value;
        context.fillRect(0, 0, 1, 1);
        const [r, g, b] = context.getImageData(0, 0, 1, 1).data;
        return `rgb(${r}, ${g}, ${b})`;
      };
      return { fg, bg, color: rgb(computed.color), background: rgb(computed.backgroundColor) };
    });
    probe.remove();
    return rows;
  }, pairs);
  const rows = sampled.map(row => ({ ...row, ratio: contrastRatio(row.color, row.background) }));
  writeFileSync(join(RESULTS, `${name}-contrast.json`), JSON.stringify(rows, null, 2));
  expect(rows.filter(row => row.ratio < 4.5)).toEqual([]);
}

for (const width of [1440, 390]) {
  for (const theme of ['light', 'dark'] as const) {
    test(`preview directions ${theme} at ${width}px`, async ({ page }) => {
      // Each of the three persisted previews reloads the production shell.
      test.setTimeout(3 * 180_000);
      await page.setViewportSize({ width, height: 900 });
      await installFixture(page);
      await page.addInitScript(theme => {
        localStorage.setItem('atp.studio.theme', theme);
        localStorage.setItem('atp.studio.openProjectChatOnEntry.v1', '0');
      }, theme);
      const errors: string[] = [];
      page.on('pageerror', error => errors.push(error.message));
      page.on('console', message => {
        if (message.type() === 'error') errors.push(`${message.text()} ${message.location().url}`);
      });
      page.on('response', response => {
        if (response.status() >= 400) errors.push(`HTTP ${response.status()}: ${response.url()}`);
      });
      page.on('request', request => {
        if (request.resourceType() === 'font') expect(new URL(request.url()).origin).toBe(new URL(page.url()).origin);
      });
      const baseline: Buffer[] = [];
      await page.goto('/#/board');
      if (width === 390) await page.getByRole('combobox', { name: 'Board lane', exact: true }).selectOption('2-ready');
      const card = page.getByTestId('task-card').filter({ hasText: 'AGT-4129' });
      await expect(card).toBeVisible();
      await expect(card.getByTestId('task-card-owner')).toContainText(OWNER);
      await capture(page, `default-before-${theme}-${width}`);
      baseline.push(await card.screenshot({ animations: 'disabled' }));
      writeFileSync(join(RESULTS, `default-card-before-${theme}-${width}--mocked.png`), baseline[0]);
      for (const id of ['preview-1', 'preview-2', 'preview-3']) {
        await page.goto('/#/workspace/settings/appearance');
        await page.getByTestId('settings-token-set').selectOption(id);
        await expect(page.locator('html')).toHaveAttribute('data-studio-token-set', `${id}.${theme}`);
        await expect.poll(() => page.evaluate(() => JSON.parse(localStorage.getItem('atp.studio.tokenSet')!).id)).toBe(id);
        await page.reload();
        await expect(page.locator('html')).toHaveAttribute('data-studio-token-set', `${id}.${theme}`);
        await expect(page.getByTestId('settings-token-set')).toHaveValue(id);
        await capture(page, `${id}-${theme}-${width}-appearance`);
        await auditContrast(page, `${id}-${theme}-${width}`);
        await page.goto('/#/board');
        if (width === 390) await page.getByRole('combobox', { name: 'Board lane', exact: true }).selectOption('2-ready');
        await expect(card).toBeVisible();
        await expect(card.getByTestId('task-card-owner')).toContainText(OWNER);
        const key = card.getByTestId('task-card-key');
        await expect(key).toBeInViewport();
        expect(await key.evaluate(el => {
          const bounds = el.getBoundingClientRect();
          const range = document.createRange();
          range.selectNodeContents(el);
          const text = range.getBoundingClientRect();
          return el.scrollWidth <= el.clientWidth + 1 && text.left >= bounds.left - 1 && text.right <= bounds.right + 1;
        })).toBe(true);
        const keyBox = (await key.boundingBox())!;
        const cardBox = (await card.boundingBox())!;
        expect(keyBox.x).toBeGreaterThanOrEqual(cardBox.x);
        expect(keyBox.x + keyBox.width).toBeLessThanOrEqual(cardBox.x + cardBox.width + 1);
        await capture(page, `${id}-${theme}-${width}-board`);
        await card.getByTestId('task-card-title').click();
        await expect(page.getByTestId('overview-title-block')).toBeVisible();
        await capture(page, `${id}-${theme}-${width}-detail`);
        if (width === 1440) {
          await page.setViewportSize({ width: 1024, height: 900 });
          await expect(page.getByTestId('studio-task-prev')).toBeInViewport();
          await expect(page.getByTestId('studio-task-next')).toBeInViewport();
          await capture(page, `${id}-${theme}-1024-header`);
          await page.setViewportSize({ width, height: 900 });
        }
        await page.goto('/#/projects/card-flow-fixture/operations');
        await expect(page.getByTestId('project-operations-area')).toBeVisible();
        await capture(page, `${id}-${theme}-${width}-project-operations`);
        await page.goto('/#/workspace/settings/operations');
        await expect(page.getByTestId('workspace-operations')).toBeVisible();
        await expect(page.getByTestId('operations-security-total')).toHaveText('6');
        await capture(page, `${id}-${theme}-${width}-operations`);
        await page.goto('/#/workspace/settings/access');
        await expect(page.getByTestId('access-area')).toBeVisible();
        await expect(page.getByTestId('access-area').getByRole('heading', { name: /^People/ })).toBeVisible();
        await capture(page, `${id}-${theme}-${width}-access`);
        await page.getByTestId('studio-attention-trigger').click();
        await expect(page.getByTestId('studio-attention-list')).toBeVisible();
        await expect(page.getByTestId('attention-row')).toHaveCount(1);
        await capture(page, `${id}-${theme}-${width}-needs-you`);
        await page.keyboard.press('Escape');
      }
      await page.goto('/#/workspace/settings/appearance');
      await page.getByTestId('settings-token-set').selectOption('studio-default');
      await page.goto('/#/board');
      if (width === 390) await page.getByRole('combobox', { name: 'Board lane', exact: true }).selectOption('2-ready');
      await expect(card).toBeVisible();
      await capture(page, `default-after-${theme}-${width}`);
      const after = await card.screenshot({ animations: 'disabled' });
      writeFileSync(join(RESULTS, `default-card-after-${theme}-${width}--mocked.png`), after);
      expect(after).toEqual(baseline[0]);
      // A linked preview leaves the saved default intact, including after theme changes.
      await page.goto('/?tokenSet=preview-3#/workspace/settings/appearance');
      await expect(page.getByTestId('settings-token-set')).toHaveValue('preview-3');
      expect(await page.evaluate(() => JSON.parse(localStorage.getItem('atp.studio.tokenSet')!).id)).toBe('studio-default');
      await page.goto('/#/workspace/settings/appearance');
      await expect(page.getByTestId('settings-token-set')).toHaveValue('studio-default');
      expect(errors).toEqual([]);
    });
  }
}

// Check actual rendered sibling boxes inside each surface. The board scrolls by
// design, so the container is the card or row being read, not the entire lane strip.
async function expectLayout(container: Locator, selector: string) {
  await expect(container).toBeVisible();
  const findings = await container.evaluate((el, selector) => {
    const parent = el.getBoundingClientRect();
    const scrolls = ['auto', 'scroll'].includes(getComputedStyle(el).overflowY);
    const top = parent.top - (scrolls ? el.scrollTop : 0);
    const bottom = scrolls ? top + Math.max(el.scrollHeight, parent.height) : parent.bottom;
    const children = [...el.querySelectorAll<HTMLElement>(selector)]
      .filter(child => child.checkVisibility() && child.offsetWidth && child.offsetHeight)
      .map(child => ({ label: child.getAttribute('data-testid') || child.className, box: child.getBoundingClientRect() }));
    const issues: string[] = [];
    for (const [i, { label, box }] of children.entries()) {
      if (box.left < parent.left - 1 || box.right > parent.right + 1 ||
          box.top < top - 1 || box.bottom > bottom + 1) issues.push(`${label} outside container`);
      for (const other of children.slice(0, i)) {
        if (Math.min(box.right, other.box.right) - Math.max(box.left, other.box.left) > 1 &&
            Math.min(box.bottom, other.box.bottom) - Math.max(box.top, other.box.top) > 1) {
          issues.push(`${label} overlaps ${other.label}`);
        }
      }
    }
    return { count: children.length, issues };
  }, selector);
  expect.soft(findings.count).toBeGreaterThan(1);
  expect.soft(findings.issues).toEqual([]);
}

for (const width of [1440, 390]) {
  for (const theme of ['light', 'dark'] as const) {
    test(`synthesis surfaces in ${theme} at ${width}px`, async ({ page }) => {
      test.setTimeout(300_000);
      await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
      await installFixture(page, true);
      await page.addInitScript(theme => {
        localStorage.setItem('atp.studio.theme', theme);
        localStorage.setItem('atp.studio.openProjectChatOnEntry.v1', '0');
      }, theme);
      const errors: string[] = [];
      page.on('pageerror', error => errors.push(error.message));
      const shot = (name: string) => capture(page, `synthesis-${name}-${width}-${theme}`);

      await page.goto('/#/workspace/settings/appearance');
      const picker = page.getByTestId('settings-token-set');
      await expect(picker).toHaveValue('studio-default');
      await picker.selectOption('preview-4');
      const html = page.locator('html');
      await expect(html).toHaveAttribute('data-studio-token-set', `preview-4.${theme}`);
      await page.reload();
      await expect(picker).toHaveValue('preview-4');
      await expect(html).toHaveAttribute('data-studio-token-set', `preview-4.${theme}`);
      await page.goto('/?fixture=welcome', { waitUntil: 'domcontentloaded' });
      await expect(page.getByTestId('studio-welcome-content')).toBeVisible();
      await expectLayout(page.locator('.studio').first(), ':scope > .studio-titlebar, :scope > .studio-body, :scope > .studio-statusbar');
      await expectLayout(page.getByTestId('cockpit-header-nav-row'), ':scope > *');
      await shot('shell');

      await page.goto('/#/board');
      if (width === 390) await page.getByRole('combobox', { name: 'Board lane', exact: true }).selectOption('2-ready');
      const card = page.getByTestId('task-card').filter({ hasText: 'AGT-4141' });
      await expect(card.getByTestId('task-card-owner')).toContainText(OWNER);
      await expectLayout(card, '[data-testid="task-card-key"], [data-testid="task-card-title"], [data-testid="task-card-owner"]');
      await expect(card.getByTestId('task-card-key')).toBeInViewport();
      await shot('board');

      await card.getByTestId('task-card-title').click();
      const detail = page.getByTestId('overview-title-block');
      await expect(detail).toContainText('Preserve claims on reconnect');
      await expectLayout(detail, '[data-testid="overview-title"], [data-testid="overview-title-subline"]');
      await expect(page.getByTestId('overview-title')).toHaveCSS('font-size', '24px');
      await shot('card');

      await page.goto('/#/projects/card-flow-fixture/operations');
      const project = page.getByTestId('project-operations-area');
      await expect(project.getByTestId('project-operations-environments')).toBeVisible();
      await expectLayout(project, ':scope > a');
      const linkColours = await project.evaluate(el => ({
        foreground: getComputedStyle(el.querySelector('a')!).color,
        background: getComputedStyle(el).backgroundColor,
      }));
      expect(contrastRatio(linkColours.foreground, linkColours.background)).toBeGreaterThanOrEqual(4.5);
      await shot('project-operations');
      await page.goto('/#/workspace/settings/operations');
      const operations = page.getByTestId('workspace-operations');
      await expect(operations.getByTestId('operations-security-total')).toHaveText('6');
      await expectLayout(operations, ':scope > header, :scope > nav');
      await expectLayout(operations.getByRole('navigation'), 'button');
      await shot('operations');
      expect(errors).toEqual([]);
    });
  }
}

test('project board loads the synthesis filter and dossier views on demand', async ({ page }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  await installFixture(page);
  await page.addInitScript(() => localStorage.setItem('atp.studio.openProjectChatOnEntry.v1', '0'));
  const capacity = { totalSlots: 3, usedSlots: 1, freeSlots: 2, ready: 1, claimableNow: 1, dependencyWidth: 1, cause: 'available' };
  await page.route('**/api/operator/coding-capacity', route => json(route, {
    fleet: { ...capacity, project: null }, projects: [{ ...capacity, project: PROJECT }],
  }));
  await page.goto('/?tokenSet=preview-4#/projects/card-flow-fixture/board');
  await page.getByTestId('studio-ab-filters').click({ timeout: 45_000 });
  await expect(page.getByTestId('kanban-filter-sidesheet-search')).toBeVisible();
  await page.getByTestId('studio-ab-explorer').click();
  const toggle = page.getByTestId('studio-board-dossier-toggle');
  await toggle.click();
  await expect(page.getByTestId('dossier-group-board')).toBeVisible();
  await toggle.click();
  await expect(page.getByTestId('task-card').filter({ hasText: 'AGT-4129' })).toBeVisible();
});
