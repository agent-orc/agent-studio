import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'NeedsInput fixture';
const WATCH_PATH = '/fixtures/needs-input';
const JOB_ID = 'AGT-2736';
const RESULTS = process.env.JOB_RESULTS_DIR ?? 'test-results';
const QUESTION = `Which deployment strategy should I implement?

- Option A: managed connector. Recommended for simpler operations.
- Option B: direct LAN deployment. Requires customer network access.

Reply with A or B to continue.`;

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**:5039/update/status', route => json(route, { isRunning: false, behindBy: 0 }));
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: false, user: null,
  }));
  await page.route('**/api/tasks/grouped**', route => json(route, {
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
  await page.route(/\/api\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route(new RegExp(`/api/tasks/${JOB_ID}(\\?|$)`), route => json(route, {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      key: JOB_ID,
      title: 'Choose connector or LAN deployment',
      state: '5e-escalated',
      order: 1,
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5.6-codex',
      createdAt: '2026-09-11T14:00:00.000Z',
      lastActivity: '2026-09-11T14:44:00.000Z',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/${JOB_ID}`,
      ownerClientId: 'local-default',
      commits: [],
      orchestratorVerdict: 'escalate',
      needsInput: {
        message: QUESTION,
        firstLine: 'Which deployment strategy should I implement?',
        runAttemptId: 'run_4e53d6d6',
        salvageBranch: 'runner/agent-runner-01/AGT-2736',
        artifactPath: 'results/needs-input.md',
      },
      parkedBlocker: {
        blockerType: 'agent-needs-input',
        lane: '5e-escalated',
        parkedAt: '2026-09-11T14:44:00.000Z',
        reason: 'choose-connector-vs-lan-deployment-strategy',
        needsInputFile: 'results/needs-input.md',
      },
    },
    promptMarkdown: 'Choose a deployment design.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    reviewEvidence: [],
  }));
}

test('NeedsInput escalation shows the full question, options, and answer field', async ({ page }, testInfo) => {
  await installRoutes(page);
  await page.goto(`/?job=${JOB_ID}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await dismissDevErrorDialog(page);

  const question = page.getByTestId('needs-input-question');
  await expect(question).toBeVisible();
  await expect(page.getByTestId('needs-input-message')).toContainText('Option A: managed connector');
  await expect(page.getByTestId('needs-input-message')).toContainText('Option B: direct LAN deployment');
  await expect(question).toContainText('runner/agent-runner-01/AGT-2736');

  mkdirSync(RESULTS, { recursive: true });
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    const path = join(RESULTS, `needs-input-escalation-${theme}--mocked.png`);
    await question.screenshot({ path });
    await testInfo.attach(`NeedsInput escalation ${theme}`, { path, contentType: 'image/png' });
  }

  await page.getByTestId('needs-input-answer').pressSequentially(
    'Choose A. Keep a LAN fallback for isolated networks.',
  );
  await expect(page.getByTestId('needs-input-answer')).toHaveValue(
    'Choose A. Keep a LAN fallback for isolated networks.',
  );
});
