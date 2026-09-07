import { test, expect, Page } from '@playwright/test';
import { dismissDevErrorDialog } from '../helpers/theme';
import * as fs from 'fs';
import * as path from 'path';

/**
 * AGT-2721 review mode in Activity across projects.
 *
 * A Watcher proposal appears in the cross-project feed as a Decision entry and
 * is answered there. It never enters Ready by itself, so the approve button is
 * the operator act that promotes the proposed card, a rejection needs a reason
 * because that reason becomes a visible suppression, and a merge needs a target.
 *
 * Fully mocked: the feed rows, the proposal, and the decision response are
 * stubbed, so the spec needs no backend, no workspace, and no Watcher sweep.
 */

const CASE_ID = 'WCH-e2e00fixture';
const PROPOSAL_ID = 'WPR-e2e00fixture';
const PROPOSED_CARD = 'AGT-9001';
const PROJECTS = ['Agent Studio'];
const WATCH_PATH = '/tmp/agent-studio';
const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
  humanReview: [], escalated: [], completed: [], archive: [],
};

function feedEntries() {
  return [
    {
      project: 'Global Watcher',
      watchPath: '',
      ts: '2026-09-06T20:05:00Z',
      kind: 'decision',
      topic: 'watcher-decision-required',
      summary: `Approve the Watcher proposal ${PROPOSED_CARD}: 15 unresolved descriptor validation errors`,
      reasoning: null,
      jobId: null,
      participantId: 'orchestrator:global-watcher',
      correlationId: CASE_ID,
      tokenUsage: null,
      userOverride: null,
    },
    {
      project: 'Global Watcher',
      watchPath: '',
      ts: '2026-09-06T20:00:00Z',
      kind: 'alert',
      topic: 'watcher-finding-raised',
      summary: '15 unresolved dossier-descriptor validation errors older than 1 d',
      reasoning: null,
      jobId: null,
      participantId: 'orchestrator:global-watcher',
      correlationId: CASE_ID,
      tokenUsage: null,
      userOverride: null,
    },
  ];
}

function proposal(decisionState: string) {
  return {
    id: PROPOSAL_ID,
    caseId: CASE_ID,
    fingerprint: 'hygiene|dossier-descriptor',
    fingerprintDigest: 'abc12345',
    detectorClass: 'hygiene',
    detectorRule:
      'Validation errors the product already computes are older than a grace period.',
    project: null,
    kind: 'new-card',
    draft: {
      title: '15 unresolved dossier-descriptor validation errors older than 1 d',
      promptMarkdown: '## Context\n\n## Changes\n\n## Acceptance\n',
      taskType: 'chore',
      tags: ['watcher-proposal', 'watcher-hygiene', 'watcher-fp-abc12345'],
      relatedTo: [],
    },
    recommendation: {
      tier: 'luna-medium',
      model: 'gpt-5.6-luna',
      thinkingLevel: 'medium',
      policyVersion: '2026-07-24',
      score: 15,
      correctnessFloorTier: null,
      reason: 'Policy 2026-07-24: chore defaults to luna-medium',
    },
    evidenceDigest: 'd41d8cd98f00b204',
    evidence: [
      {
        label: 'Validator',
        value: 'dossier-descriptor',
        source: 'dossier-descriptor',
        available: true,
      },
    ],
    modelCalls: [],
    createdTaskKey: PROPOSED_CARD,
    commentedOnTaskKey: null,
    decision: {
      state: decisionState,
      decidedAtUtc: decisionState === 'pending' ? null : '2026-09-06T20:10:00Z',
      decidedBy: decisionState === 'pending' ? null : 'local-default',
      reason: null,
      mergedIntoTaskKey: null,
    },
    createdAtUtc: '2026-09-06T20:05:00Z',
    updatedAtUtc: '2026-09-06T20:05:00Z',
  };
}

async function mockStudio(page: Page, decisions: unknown[]): Promise<void> {
  await page.route('**/update/status', route =>
    route.fulfill({ json: { phase: 'idle', isRunning: false, behindBy: 0 } })
  );
  await page.route('**/hubs/jobs/negotiate**', route =>
    route.fulfill({
      json: {
        connectionId: 'watcher-review-e2e',
        connectionToken: 'watcher-review-e2e',
        negotiateVersion: 1,
        availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
      },
    })
  );
  await page.routeWebSocket('**/hubs/jobs**', socket => {
    socket.onMessage(message => {
      if (message.toString().includes('"protocol":"json"')) socket.send('{}');
    });
  });

  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    const json = (body: unknown) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

    if (url.pathname === `/api/watcher/proposals/${PROPOSAL_ID}/decision`) {
      decisions.push(JSON.parse(route.request().postData() ?? '{}'));
      return json(proposal('approved'));
    }
    if (url.pathname === '/api/watcher/proposals') return json({ proposals: [proposal('pending')] });
    if (url.pathname === '/api/runner/orchestrator-feed') return json({ entries: feedEntries() });
    if (url.pathname === '/api/auth/status') {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.pathname === '/api/runner/global/orchestrator-session') {
      return json({ project: '(global)', session: null });
    }
    if (url.pathname === '/api/watch-paths') {
      return json(PROJECTS.map(name => ({ name, path: WATCH_PATH, rootPath: WATCH_PATH })));
    }
    if (url.pathname === '/api/tasks/grouped') return json(EMPTY_GROUPED);
    if (url.pathname === '/api/tasks/archive') return json({ items: [], total: 0 });
    if (url.pathname === '/api/tasks') return json([]);
    if (url.pathname === '/api/runner/status') return json({ projects: {} });
    if (url.pathname === '/api/runner/pickup-gates') return json({ projects: {} });
    if (url.pathname === '/api/projects') return json([]);
    if (url.pathname === '/api/workspaces') return json([]);
    if (url.pathname === '/api/epics') return json([]);
    if (url.pathname === '/api/epics/completed/count') return json({ count: 0 });
    if (url.pathname === '/api/cli/quota') return json({ snapshots: [], ttlSeconds: 600 });
    if (/\/api\/cli\/[^/]+\/models$/.test(url.pathname)) return json({ models: [], source: 'watcher-e2e' });
    if (url.pathname === '/api/crash-recovery/pending') return json({ pending: [] });
    if (url.pathname === '/api/orchestrator/sessions') return json({ sessions: [] });
    if (url.pathname === '/api/workspace/tokens/expensive-jobs') return json({ jobs: [] });
    // Endpoints whose consumers expect a bare array. Falling through to an
    // object here paints the global error dialog over the shell.
    if (url.pathname.startsWith('/api/v1/management/remote-hosts')) return json([]);
    if (url.pathname === '/api/tags') return json([]);
    if (url.pathname === '/api/clients' || url.pathname === '/api/clients/') return json([]);
    if (/^\/api\/bus\/[^/]+\/messages$/.test(url.pathname)) return json([]);
    return json({});
  });
}

async function openWatcherDecision(page: Page) {
  await page.addInitScript(() => {
    localStorage.removeItem('atp.studio.tabs.v1');
    localStorage.setItem('activeProjects', '[]');
  });
  await page.setViewportSize({ width: 1440, height: 960 });
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await dismissDevErrorDialog(page);

  await page.getByTestId('studio-ab-activity').click();
  const row = page
    .getByTestId('orchestrator-feed-entry')
    .filter({ hasText: 'Approve the Watcher proposal' })
    .first();
  await expect(row).toBeVisible();
  await row.click();

  const panel = page.getByTestId('watcher-decision');
  await expect(panel).toBeVisible();
  return panel;
}

// The production bundle registers an Angular service worker. A service worker
// serves fetches itself and therefore bypasses page routing, which would let
// the real network answer the stubbed endpoints when the spec runs against a
// production build. Blocking it keeps this fully-mocked spec deterministic on
// both the dev and the stable target.
test.use({ serviceWorkers: 'block' });

test.describe('Activity across projects / Watcher review mode', () => {
  test('a Watcher proposal renders as a Decision entry with its case and recommendation', async ({ page }) => {
    await mockStudio(page, []);

    const panel = await openWatcherDecision(page);

    await expect(panel.getByTestId('watcher-decision-case')).toHaveText(CASE_ID);
    await expect(panel.getByTestId('watcher-decision-card')).toHaveText(PROPOSED_CARD);
    await expect(panel.getByTestId('watcher-decision-class')).toHaveText('hygiene');
    await expect(panel.getByTestId('watcher-decision-model')).toContainText('gpt-5.6-luna');
    await expect(panel.getByTestId('watcher-decision-rule')).toContainText('grace period');
  });

  test('the Decision entry carries the four review-mode answers', async ({ page }, testInfo) => {
    await mockStudio(page, []);

    const panel = await openWatcherDecision(page);

    await expect(panel.getByTestId('watcher-decision-approve')).toBeVisible();
    await expect(panel.getByTestId('watcher-decision-edit')).toBeVisible();
    await expect(panel.getByTestId('watcher-decision-merge-start')).toBeVisible();
    await expect(panel.getByTestId('watcher-decision-reject')).toBeVisible();

    const resultsDir = process.env['JOB_RESULTS_DIR'];
    if (resultsDir) {
      const file = path.join(resultsDir, 'watcher-decision-entry.png');
      fs.mkdirSync(resultsDir, { recursive: true });
      await panel.screenshot({ path: file });
      testInfo.attach('watcher-decision-entry', { path: file, contentType: 'image/png' });
    }
  });

  test('approving posts the decision and shows the recorded answer', async ({ page }) => {
    const decisions: unknown[] = [];
    await mockStudio(page, decisions);

    const panel = await openWatcherDecision(page);
    await panel.getByTestId('watcher-decision-approve').click();
    await panel.getByTestId('watcher-decision-confirm').click();

    await expect(panel.getByTestId('watcher-decision-answered')).toContainText('Approved');
    expect(decisions).toHaveLength(1);
    expect(decisions[0]).toMatchObject({ decision: 'approved' });
  });

  test('a rejection cannot be confirmed without a reason', async ({ page }) => {
    await mockStudio(page, []);

    const panel = await openWatcherDecision(page);
    await panel.getByTestId('watcher-decision-reject').click();

    // The reason feeds a visible, expiring suppression, so it is required.
    await expect(panel.getByTestId('watcher-decision-reason')).toBeVisible();
    await expect(panel.getByTestId('watcher-decision-confirm')).toBeDisabled();

    await panel.getByTestId('watcher-decision-reason').fill('planned maintenance window');
    await expect(panel.getByTestId('watcher-decision-confirm')).toBeEnabled();
  });

  test('a merge cannot be confirmed without a target card', async ({ page }) => {
    await mockStudio(page, []);

    const panel = await openWatcherDecision(page);
    await panel.getByTestId('watcher-decision-merge-start').click();

    await expect(panel.getByTestId('watcher-decision-merge')).toBeVisible();
    await expect(panel.getByTestId('watcher-decision-confirm')).toBeDisabled();

    await panel.getByTestId('watcher-decision-merge').fill('AGT-2717');
    await expect(panel.getByTestId('watcher-decision-confirm')).toBeEnabled();
  });
});
