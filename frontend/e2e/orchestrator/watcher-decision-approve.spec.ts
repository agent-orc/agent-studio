import type { Page } from '@playwright/test';
import { expect, test } from '../fixtures/dev-backend';
import { dismissDevErrorDialog } from '../helpers/theme';

const PROJECT = 'Agent Studio';
const WATCH_PATH = '/tmp/agent-studio';
const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
  humanReview: [], escalated: [], completed: [], archive: [],
};

const PROPOSAL = {
  id: 'WPR-000001',
  caseId: 'WCH-000001',
  detectorClass: 'repetition',
  fingerprint: 'rep-abc123',
  project: PROJECT,
  jobId: 'watcher-approve-job',
  isComment: false,
  commentedJobId: null,
  title: '[Watcher] Repeated failure: integration blocked on dirty checkout',
  recommendedModel: 'gpt-5.6-terra',
  recommendedThinkingLevel: 'medium',
  tags: ['watcher-proposal', 'watcher-repetition'],
  createdAtUtc: '2026-09-06T20:00:00Z',
  decision: null as null | { outcome: string; reason: string | null; mergedIntoJobId: string | null; decidedAtUtc: string; decidedBy: string },
};

const FEED_ENTRY = {
  ts: '2026-09-06T20:05:00Z',
  kind: 'decision',
  topic: 'watcher-decision-required',
  summary: `Watcher proposal ${PROPOSAL.id}: ${PROPOSAL.title}`,
  reasoning: null,
  project: PROJECT,
  watchPath: WATCH_PATH,
  jobId: PROPOSAL.jobId,
};

/**
 * Review-mode approve flow (orchestrator-waechter dossier §10.4): a Watcher
 * proposal's Decision entry in Activity across projects shows the
 * recommended model and approve/edit/merge/reject controls. Approving posts
 * the decision through the review-mode API - the proposal never enters Ready
 * by itself.
 */
async function mockStudio(page: Page, capture: (body: unknown) => void): Promise<void> {
  await page.route('**/update/status', route => route.fulfill({
    json: { phase: 'idle', isRunning: false, behindBy: 0 },
  }));
  await page.route('**/hubs/jobs/negotiate**', route => route.fulfill({
    json: {
      connectionId: 'watcher-decision-e2e',
      connectionToken: 'watcher-decision-e2e',
      negotiateVersion: 1,
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
    },
  }));
  await page.routeWebSocket('**/hubs/jobs**', socket => {
    socket.onMessage(message => {
      if (message.toString().includes('"protocol":"json"')) socket.send('{}');
    });
  });

  let proposal = { ...PROPOSAL };

  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    const json = (body: unknown) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });

    if (url.pathname === '/api/auth/status') {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.pathname === '/api/runner/orchestrator-feed') return json({ entries: [FEED_ENTRY] });
    if (url.pathname === '/api/runner/global/orchestrator-session') {
      return json({ project: '(global)', session: null });
    }
    if (url.pathname === '/api/watch-paths') {
      return json([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    }
    if (url.pathname === '/api/tasks/grouped') return json(EMPTY_GROUPED);
    if (url.pathname === '/api/tasks/archive') return json({ items: [], total: 0 });
    if (url.pathname === '/api/tasks') return json([]);
    if (url.pathname === '/api/runner/status') return json({ projects: {} });
    if (url.pathname === '/api/runner/pickup-gates') return json({ projects: {} });
    if (url.pathname === '/api/workspaces') {
      return json([{
        id: 'workspace-1', displayName: 'Workspace', sortOrder: 0, isDefault: true,
        color: null, createdAt: '2026-09-06T07:00:00Z',
        projects: [{
          sourceType: 'local-folder', id: 'project-1', displayName: PROJECT, shortCode: 'AGT',
          workspaceId: 'workspace-1', color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
          storageLocation: WATCH_PATH, repositoryPath: null, rootPath: WATCH_PATH, repositoryUrl: null,
          urls: [], archived: false, createdAt: '2026-09-06T07:00:00Z',
        }],
      }]);
    }
    if (url.pathname === '/api/projects') return json([]);
    if (/^\/api\/bus\/[^/]+\/messages$/.test(url.pathname)) return json([]);
    if (url.pathname === '/api/tags' || url.pathname === '/api/clients' || url.pathname === '/api/clients/') return json([]);
    if (url.pathname === '/api/orchestrator/sessions') return json({ sessions: [] });
    if (url.pathname === '/api/epics') return json([]);
    if (url.pathname === '/api/epics/completed/count') return json({ count: 0 });
    if (url.pathname === '/api/cli/quota') return json({ snapshots: [], ttlSeconds: 600 });
    if (/\/api\/cli\/[^/]+\/models$/.test(url.pathname)) return json({ models: [], source: 'watcher-decision-e2e' });
    if (url.pathname === '/api/crash-recovery/pending') return json({ pending: [] });
    if (url.pathname === '/api/v1/management/remote-hosts') return json([]);
    if (url.pathname === '/api/v1/management/remote-hosts/link-health') return json([]);

    if (url.pathname === '/api/watcher/status') {
      return json({
        options: { enabled: true, intervalSeconds: 300, persistenceSweepsBeforeProposal: 2, suppressionDays: 14 },
        lastRun: { lastRunAtUtc: '2026-09-06T20:05:00Z', sweepId: 's1', enabled: true, observationsCollected: 1, casesUpdated: 1, proposalsCreated: 1, commentsAppended: 0, contingentBlocked: 0, analysisCalls: 0 },
        contingent: {
          budgets: { dailyTokenBudget: 200000, weeklyTokenBudget: 1000000, dailyProposalBudget: 10, weeklyProposalBudget: 40, dailyModelCallBudget: 20, weeklyModelCallBudget: 100 },
          day: { periodKey: 'day:2026-09-06', tokensUsed: 0, modelCalls: 0, proposalsCreated: 1, commentsAppended: 0 },
          week: { periodKey: 'week:2026-W36', tokensUsed: 0, modelCalls: 0, proposalsCreated: 1, commentsAppended: 0 },
          modelCallsExhausted: false,
          proposalsExhausted: false,
        },
      });
    }
    if (url.pathname === '/api/watcher/proposals' && route.request().method() === 'GET') {
      return json({ proposals: [proposal] });
    }
    if (/\/api\/watcher\/proposals\/[^/]+\/decision$/.test(url.pathname) && route.request().method() === 'POST') {
      const body = route.request().postDataJSON();
      capture(body);
      proposal = { ...proposal, decision: { outcome: body.outcome, reason: body.reason ?? null, mergedIntoJobId: body.mergedIntoJobId ?? null, decidedAtUtc: '2026-09-06T20:10:00Z', decidedBy: 'local-default' } };
      return json({ proposal });
    }

    return json({});
  });
}

test('Watcher Decision entry: approve moves the recommendation into a captured decision call', async ({ page, devBackend }) => {
  void devBackend;
  let captured: unknown = null;
  await page.addInitScript(() => {
    localStorage.setItem('atp.orchestrator-feed.alerts-seen-at', '2026-01-01T00:00:00Z');
    localStorage.removeItem('atp.studio.tabs.v1');
    localStorage.setItem('activeProjects', '[]');
  });
  await mockStudio(page, body => { captured = body; });
  await page.setViewportSize({ width: 1440, height: 960 });
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await dismissDevErrorDialog(page);

  await page.getByTestId('studio-ab-activity').click();
  await expect(page).toHaveURL(/#\/feed$/);

  const entry = page.getByTestId('orchestrator-feed-entry').first();
  await expect(entry).toBeVisible();
  await entry.click();

  const panel = page.getByTestId('watcher-decision-panel');
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('gpt-5.6-terra');
  await expect(panel).toContainText('repetition');

  await page.getByTestId('watcher-approve').click();

  await expect.poll(() => captured).not.toBeNull();
  expect(captured).toMatchObject({ outcome: 'approved' });

  await expect(page.getByTestId('watcher-decision-outcome')).toContainText('approved');
});

test('Watcher Decision entry: reject requires a reason before it can be submitted', async ({ page, devBackend }) => {
  void devBackend;
  let captured: unknown = null;
  await page.addInitScript(() => {
    localStorage.setItem('atp.orchestrator-feed.alerts-seen-at', '2026-01-01T00:00:00Z');
    localStorage.removeItem('atp.studio.tabs.v1');
    localStorage.setItem('activeProjects', '[]');
  });
  await mockStudio(page, body => { captured = body; });
  await page.setViewportSize({ width: 1440, height: 960 });
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await dismissDevErrorDialog(page);

  await page.getByTestId('studio-ab-activity').click();
  await page.getByTestId('orchestrator-feed-entry').first().click();
  await expect(page.getByTestId('watcher-decision-panel')).toBeVisible();

  await page.getByTestId('watcher-reject-start').click();
  const confirm = page.getByTestId('watcher-reject-confirm');
  await expect(confirm).toBeDisabled();

  await page.getByTestId('watcher-reject-reason').fill('Known noise: planned maintenance window.');
  await expect(confirm).toBeEnabled();
  await confirm.click();

  await expect.poll(() => captured).not.toBeNull();
  expect(captured).toMatchObject({ outcome: 'rejected', reason: 'Known noise: planned maintenance window.' });
});
