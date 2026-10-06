import { expect, test, type Page, type TestInfo } from '@playwright/test';
import { mkdirSync, readFileSync, writeFileSync } from 'fs';
import * as path from 'path';

const PROJECT = 'fixture-decision-surface';
const WATCH_PATH = 'C:/fixtures/decision-surface';
const JOB_ID = 'AGT-2355-icon-pick';
const DECISION_HTML = readFileSync(
  path.resolve(__dirname, '../fixtures/decision-surface/icon-pick-decision.html'),
  'utf8',
);
const DECISION_JSON_MATCH = DECISION_HTML.match(
  /<script type="application\/json" data-agent-studio-decision>([\s\S]*?)<\/script>/,
);
if (!DECISION_JSON_MATCH) throw new Error('The icon decision fixture has no embedded contract.');
const DECISION_JSON = DECISION_JSON_MATCH[1].trim();
const SHOTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve(__dirname, '../../test-results/decision-surface');

interface CapturedMutation {
  body: Record<string, unknown>;
  headers: Record<string, string>;
}

function taskInfo() {
  return {
    id: JOB_ID,
    taskKey: `${WATCH_PATH}::${JOB_ID}`,
    title: 'Choose icons for the compact action row',
    state: '5e-escalated',
    orchestratorVerdict: 'escalate',
    agent: 'codex',
    cliType: 'codex',
    model: null,
    thinkingLevel: null,
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/5e-escalated/${JOB_ID}`,
    execution: null,
    kind: 'task',
    epicId: null,
    commit: null,
    commits: [],
    mergeSignal: null,
    tags: ['ui', 'icons'],
    ownerClientId: 'local-default',
    lastUsage: null,
  };
}

function taskDetail() {
  return {
    info: taskInfo(),
    promptMarkdown: '# Icon decision\n\nChoose one icon source for the compact action row.',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown:
      '# Result\n\nThe implementation is ready except for the icon-family choice.',
    statusGeneration: null,
    contextUsage: null,
    log: [],
    summaryState: {
      status: 'ready',
      startedAt: null,
      finishedAt: null,
      errorMessage: null,
      bytesWritten: 70,
    },
    reviewEvidence: [],
  };
}

async function installRoutes(
  page: Page,
  capture: (kind: 'continue' | 'move', mutation: CapturedMutation) => void,
  artifact: 'html' | 'json' = 'html',
): Promise<void> {
  const info = taskInfo();
  const grouped = {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    review: [],
    autoReview: [],
    humanReview: [],
    escalated: [info],
    completed: [],
    archive: [],
  };

  await page.route('**/api/**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: '[]',
    }).catch(() => undefined));
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      }),
    }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        name: PROJECT,
        path: WATCH_PATH,
        rootPath: WATCH_PATH,
        repositoryPath: WATCH_PATH,
      }]),
    }));
  await page.route('**/api/projects/settings**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/projects/*/workbenches**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{"items":[]}' }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        isDev: false,
        devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
      }),
    }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ projects: {} }),
    }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-07-28T12:00:00Z', snapshots: [] }),
    }));
  await page.route('**/api/usage/cockpit**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        snapshotVersion: 1,
        workspaceId: 'fixture',
        timeZone: 'UTC',
        weekStart: 1,
        generatedAt: '2026-10-04T15:14:00Z',
        calendar: { timeZone: 'UTC', weekStart: 1 },
        clis: [],
        cost: { currency: 'USD', todayUsd: null, weekUsd: null, projects: [],
          coverage: { status: 'unavailable', observedAt: null, ttlSeconds: null } },
        runs: [],
        slots: [],
        sources: {},
      }),
    }));
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(grouped),
    }));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${JOB_ID}(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(taskDetail()),
    }));
  await page.route(/\/api\/tasks\/[^/]+\/pipeline(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(`**/api/tasks/${JOB_ID}/output**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(`**/api/tasks/${JOB_ID}/runs**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        runCount: 0,
        firstStartedAt: null,
        lastActivityAt: null,
        hasActiveRun: false,
        runs: [],
        promptEntries: [],
        runnerEvents: [],
      }),
    }));
  await page.route(`**/api/tasks/${JOB_ID}/session-events**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ events: [], sessionChain: [] }),
    }));
  await page.route(`**/api/tasks/${JOB_ID}/screenshots**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ jobId: JOB_ID, screenshots: [] }),
    }));
  await page.route(`**/api/tasks/${JOB_ID}/git/status**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        isRepo: true,
        branch: 'main',
        filesChanged: 0,
        totalAdded: 0,
        totalRemoved: 0,
        files: [],
        error: null,
      }),
    }));
  await page.route('**/code-review/list**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ entries: [] }),
    }));
  await page.route('**/files/orchestrator-follow-up.md**', (route) =>
    route.fulfill({ status: 404, contentType: 'text/plain', body: '' }));
  await page.route('**/timeline**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/files/results/decision.json**', (route) =>
    route.fulfill(artifact === 'json'
      ? { status: 200, contentType: 'application/json', body: DECISION_JSON }
      : { status: 404, contentType: 'text/plain', body: '' }));
  await page.route('**/files/results/decision.html**', (route) =>
    route.fulfill(artifact === 'html'
      ? { status: 200, contentType: 'text/html', body: DECISION_HTML }
      : { status: 404, contentType: 'text/plain', body: '' }));
  await page.route(`**/api/v1/projects/*/tasks/${JOB_ID}/continue**`, async (route) => {
    capture('continue', {
      body: route.request().postDataJSON() as Record<string, unknown>,
      headers: route.request().headers(),
    });
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ status: 'queued', execution: null }),
    });
  });
  await page.route(`**/api/v1/projects/*/tasks/${JOB_ID}/move**`, async (route) => {
    capture('move', {
      body: route.request().postDataJSON() as Record<string, unknown>,
      headers: route.request().headers(),
    });
    await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
  });
}

async function openDecision(
  page: Page,
  capture: (kind: 'continue' | 'move', mutation: CapturedMutation) => void,
  artifact: 'html' | 'json' = 'html',
): Promise<void> {
  await page.setViewportSize({ width: 1480, height: 1600 });
  await installRoutes(page, capture, artifact);
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    { waitUntil: 'domcontentloaded', timeout: 60_000 });
  await expect(page.getByTestId('decision-surface')).toBeVisible({ timeout: 20_000 });
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((value) => {
    document.documentElement.dataset['studioTheme'] = value;
    localStorage.setItem('atp.studio.theme', value);
  }, theme);
}

async function saveDecisionShot(
  page: Page,
  testInfo: TestInfo,
  theme: 'dark' | 'light',
): Promise<void> {
  await setTheme(page, theme);
  await page.waitForTimeout(150);
  const surface = page.getByTestId('decision-surface');
  await surface.scrollIntoViewIfNeeded();
  const image = await surface.screenshot();
  const name = `decision-surface-icon-pick-${theme}--mocked.png`;
  await testInfo.attach(name, { body: image, contentType: 'image/png' });
  mkdirSync(SHOTS_DIR, { recursive: true });
  writeFileSync(path.join(SHOTS_DIR, name), image);
}

test.describe('operator decision surface', () => {
  test.beforeEach(() => test.setTimeout(150_000));

  test('shows the older brief delivery choices in both themes', async ({ page }) => {
    await openDecision(page, () => undefined);
    await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${JOB_ID}(\\?|$)`), (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          ...taskDetail(),
          info: {
            ...taskInfo(),
            olderBriefDelivery: {
              attemptId: 'run_older_brief',
              briefVersion: 'old-version',
              currentBriefVersion: 'new-version',
              resultRef: 'refs/heads/agent-studio/result/run_older_brief',
              status: 'pending',
              offeredAtUtc: '2026-10-04T15:14:00Z',
            },
          },
        }),
      }));
    await page.reload();

    const offer = page.getByTestId('older-brief-delivery-offer');
    await expect(offer).toBeVisible();
    await expect(offer).toContainText('Delivered against an older brief');
    await expect(offer).toContainText('refs/heads/agent-studio/result/run_older_brief');
    await expect(page.getByTestId('older-brief-accept')).toBeVisible();
    await expect(page.getByTestId('older-brief-starting-point')).toBeVisible();
    await expect(page.getByTestId('older-brief-discard')).toBeVisible();
    await expect(page.getByTestId('older-brief-accept')).toHaveCSS('border-top-width', '1px');
    mkdirSync(SHOTS_DIR, { recursive: true });
    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await offer.screenshot({ path: path.join(SHOTS_DIR, `older-brief-delivery-${theme}--mocked.png`) });
    }
  });

  test('marks a Ready card while its remote run is still live', async ({ page }) => {
    await page.setViewportSize({ width: 1480, height: 1000 });
    await installRoutes(page, () => undefined);
    const steered = {
      ...taskInfo(),
      state: '2-ready',
      executionLocation: {
        state: 'remote-running',
        executionKind: 'remote',
        runnerId: 'agent-runner-01',
        startedAt: new Date().toISOString(),
        lastHeartbeat: new Date().toISOString(),
        lastActivityAt: new Date().toISOString(),
        connectionState: 'connected',
        leaseState: 'active',
        trustReason: 'The steered remote run still holds its lease.',
      },
    };
    await page.route('**/api/v1/studio/board**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          backlog: [], preparation: [], orchestratorPrep: [], ready: [steered],
          progress: [], failedPickup: [], codeNotComplete: [], review: [],
          autoReview: [], humanReview: [], escalated: [], completed: [], archive: [],
        }),
      }));
    await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 60_000 });
    const badge = page.getByTestId('task-card-live-remote-outside-progress');
    await expect(badge).toBeVisible();
    // The broad API fixture leaves unrelated shell reads incomplete. Its
    // generic error dialog is hidden only for this visual capture.
    await page.addStyleTag({ content: 'app-dialog[role="alertdialog"] { display: none !important; }' });
    mkdirSync(SHOTS_DIR, { recursive: true });
    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await page.mouse.move(0, 0);
      await page.screenshot({ path: path.join(SHOTS_DIR, `ready-card-live-remote-${theme}--mocked.png`) });
    }
  });

  test('steers the recommended icon choice through the existing endpoint', async ({ page }) => {
    let mutation: CapturedMutation | null = null;
    await openDecision(page, (kind, captured) => {
      if (kind === 'continue') mutation = captured;
    });

    const recommended = page.getByTestId('decision-option-lucide');
    await expect(recommended.locator('input')).toBeChecked();
    await page.getByTestId('decision-steer').fill('Keep the close icon at 16 px.');
    await page.getByTestId('decision-submit').click();
    await expect.poll(() => mutation).not.toBeNull();

    expect(mutation!.body['mode']).toBe('steer');
    expect(mutation!.body['prompt']).toContain('Selected option: Use Lucide (lucide)');
    expect(mutation!.body['prompt']).toContain('Keep the close icon at 16 px.');
    expect(mutation!.headers['x-client-id']).toBe('local-default');
  });

  test('shows the isolated icon comparison in both themes', async ({ page }, testInfo) => {
    await openDecision(page, () => undefined);

    await expect(page.getByTestId('decision-surface-title')).toHaveText(
      'Choose the icon source',
    );
    await expect(page.getByTestId('decision-surface-recommendation')).toContainText(
      'Lucide matches',
    );
    const frame = page.getByTestId('decision-surface-frame');
    await expect(frame).toHaveAttribute('sandbox', 'allow-scripts');
    await expect(
      page.frameLocator('[data-testid="decision-surface-frame"]').getByRole('heading', {
        name: 'Pick one icon language for the task action',
      }),
    ).toBeVisible();

    await saveDecisionShot(page, testInfo, 'dark');
    await saveDecisionShot(page, testInfo, 'light');
  });

  test('generates the trusted action form from standalone decision.json', async ({ page }) => {
    await openDecision(page, () => undefined, 'json');

    await expect(page.getByTestId('decision-surface-artifact')).toHaveText('decision.json');
    await expect(page.getByTestId('decision-surface-frame')).toHaveCount(0);
    await expect(page.getByTestId('decision-option-lucide')).toContainText(
      'Use the existing outline family',
    );
    await expect(page.getByTestId('decision-option-keep-current')).toBeVisible();
  });

  test('moves a terminal choice with the selection and guidance as its reason', async ({
    page,
  }) => {
    let mutation: CapturedMutation | null = null;
    await openDecision(page, (kind, captured) => {
      if (kind === 'move') mutation = captured;
    });

    await page.getByTestId('decision-option-keep-current').click();
    await page.getByTestId('decision-steer').fill('Record this as the accepted exception.');
    await expect(page.getByTestId('decision-submit')).toHaveText('Apply and accept');
    await page.getByTestId('decision-submit').click();
    await expect.poll(() => mutation).not.toBeNull();

    expect(mutation!.body['targetState']).toBe('6-completed');
    expect(mutation!.body['reason']).toContain('selected "Keep current glyphs"');
    expect(mutation!.body['reason']).toContain('Record this as the accepted exception.');
    expect(mutation!.headers['x-client-id']).toBe('local-default');
  });
});
