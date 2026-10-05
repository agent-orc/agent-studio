import { expect, test, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'Failure details fixture';
const WATCH_PATH = 'C:/fixtures/failure-details';
const JOB_ID = 'overview-failure-details';
const RAW = '[orchestrator] [watchdog-timeout] "Persistent anchored review comments" (codex): auto-cancelled after 601s of silence. The run will finalize as failed. [phase=TurnCompleted silence=601s allowed=600s session=019c123456789abcdef complete-tail]';

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

function detail() {
  return {
    info: {
      id: JOB_ID, taskKey: `${WATCH_PATH}::${JOB_ID}`, key: 'FIX-1', title: 'Failure details regression',
      state: '5-human-review', order: 1, agent: 'codex', cliType: 'codex', model: 'gpt-5.6-sol',
      createdAt: '2026-07-22T09:00:00.000Z', lastActivity: '2026-07-22T09:20:01.000Z',
      watchPath: WATCH_PATH, projectName: PROJECT, folderPath: `${WATCH_PATH}/${JOB_ID}`,
      sessionName: null, useOwnSession: null, lastUsage: null, execution: null, commit: null, commits: [],
      ownerClientId: 'local-default',
      outcomeIssue: {
        kind: 'watchdog-timeout', label: 'Watchdog timeout', severity: 'High',
        summary: `${RAW.slice(0, 257)}...`, technicalDetails: RAW,
        lastSeenAt: '2026-07-22T09:20:01.000Z',
      },
    },
    promptMarkdown: 'Test prompt.', statusMarkdown: '', log: [], promptHistory: [],
    contextUsage: null, reviewEvidence: [], summaryState: null,
  };
}

async function installRoutes(page: Page): Promise<void> {
  const escapedId = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: false, user: null,
  }));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    codeNotComplete: [], review: [], autoReview: [], humanReview: [], escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/clients', route => json(route, [
    { id: 'local-default', displayName: 'Local', kind: 'agent-instance' },
  ]));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route(new RegExp(`/api/tasks/${escapedId}/output(\\?|$)`), route => json(route, []));
  await page.route(new RegExp(`/api/tasks/${escapedId}/runs(\\?|$)`), route => json(route, { runs: [] }));
  await page.route(new RegExp(`/api/tasks/${escapedId}/session-events(\\?|$)`), route => json(route, {
    events: [], sessionChain: [],
  }));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${escapedId}(\\?|$)`), route => json(route, detail()));
}

test('Overview failure uses human copy and preserves the full raw diagnostic behind details', async ({ page }, testInfo) => {
  await installRoutes(page);
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  const failure = page.getByTestId('overview-failure-reason');
  await expect(failure).toBeVisible();

  const primary = page.getByTestId('overview-failure-primary');
  await expect(primary).toHaveText('Run automatically stopped after 10 minutes without progress (watchdog).');
  await expect(primary).not.toContainText('phase=');
  await expect(page.getByTestId('overview-failure-raw')).not.toBeVisible();

  await dismissDevErrorDialog(page);
  await failure.getByText('Show technical details').click();
  await expect(page.getByTestId('overview-failure-raw')).toBeVisible();
  await expect(page.getByTestId('overview-failure-raw')).toHaveText(RAW);

  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await failure.screenshot({ path: testInfo.outputPath(`overview-failure-${theme}.png`) });
  }
});
