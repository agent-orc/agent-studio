import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'fs';
import * as path from 'path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-2749 — why a card is parked in Human Review.
 *
 * On 2026-09-06 thirteen cards were parked as product failures when every one
 * of them was an infrastructure or quota fault, and the lane exposed neither
 * the class nor a retry counter. The card now carries a failure-class chip
 * (`Infrastructure · retry 2/3`) for a non-product class, and stays quiet for a
 * product failure and for a card that carries no class at all.
 *
 * Fully mocked through `page.route('**\/api\/**')`, so the spec needs a served
 * frontend but no backend.
 */

const PROJECT = 'fixture-failure-class';
const WATCH_PATH = 'C:/fixtures/failure-class';

const SHOTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? process.env.JOB_RESULTS_DIR
  : path.resolve(__dirname, '../../test-results/human-review-failure-class');

/** Persist a shot as a report attachment and as a durable file under results/. */
async function saveShot(testInfo: TestInfo, name: string, body: Buffer): Promise<void> {
  await testInfo.attach(name, { body, contentType: 'image/png' });
  try {
    mkdirSync(SHOTS_DIR, { recursive: true });
    writeFileSync(path.join(SHOTS_DIR, name), body);
  } catch {
    /* best-effort: the attachment above is the fallback */
  }
}

const INFRA_TITLE = 'Gate cut off by its budget';
const QUOTA_TITLE = 'CLI provider quota exhausted';
const PRODUCT_TITLE = 'Two tests fail on the change';
const LEGACY_TITLE = 'Parked before the failure taxonomy';

function task(id: string, key: string, title: string, order: number, failure: unknown) {
  return {
    id,
    key,
    displayKey: key,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state: '5-human-review',
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-09-06T08:00:00Z',
    lastActivity: '2026-09-06T09:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/5-human-review/${id}`,
    ownerClientId: 'local-default',
    commits: [],
    tags: [],
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    integration: {
      status: 'pending',
      deliveryRef: `task/${id}`,
      sha: null,
      integrationBranch: 'develop',
      detail: null,
      failure,
    },
  };
}

const INFRA_CARD = task('fc-infra', 'AGT-2749', INFRA_TITLE, 1, {
  code: 'gate-failed',
  label: 'Gate failed',
  reason: 'The gate run was cut off by its budget before it could finish.',
  rebaseRecoveryAvailable: false,
  failureClass: 'infrastructure',
  failureSignature: 'gate-budget-exceeded',
  retryAttempt: 2,
  retryBudget: 3,
});
const QUOTA_CARD = task('fc-quota', 'AGT-2750', QUOTA_TITLE, 2, {
  code: 'review-failed',
  label: 'Review failed',
  reason: 'The CLI provider quota is exhausted.',
  rebaseRecoveryAvailable: false,
  failureClass: 'quota',
  failureSignature: 'cli-quota-exhausted',
  retryAttempt: 3,
  retryBudget: 3,
});
const PRODUCT_CARD = task('fc-product', 'AGT-2751', PRODUCT_TITLE, 3, {
  code: 'gate-failed',
  label: 'Gate failed',
  reason: '2 test(s) fail on the change that pass on the baseline.',
  rebaseRecoveryAvailable: false,
  failureClass: 'product',
  failureSignature: 'new-test-failures',
  retryAttempt: 0,
  retryBudget: 3,
});
const LEGACY_CARD = task('fc-legacy', 'AGT-2752', LEGACY_TITLE, 4, {
  code: 'gate-failed',
  label: 'Gate failed',
  reason: 'The gate run failed.',
  rebaseRecoveryAvailable: false,
});

const HUMAN_REVIEW = [INFRA_CARD, QUOTA_CARD, PRODUCT_CARD, LEGACY_CARD];

const GROUPED = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [],
  failedPickup: [],
  codeNotComplete: [],
  autoReview: [],
  review: [],
  humanReview: HUMAN_REVIEW,
  escalated: [],
  completed: [],
  archive: [],
};

async function json(route: Route, body: unknown): Promise<void> {
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  // Catch-all first (lowest priority); the specific routes below win.
  await page.route('**/api/**', (route) => json(route, []));
  await page.route('**/api/tasks/archive**', (route) => json(route, {
    items: [], total: 0, offset: 0, limit: 50,
  }));
  await page.route('**/api/auth/status', (route) => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route(/\/api\/tasks(\?|$)/, (route) => json(route, HUMAN_REVIEW));
  await page.route('**/api/tasks/grouped**', (route) => json(route, GROUPED));
  await page.route('**/api/watch-paths**', (route) => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', (route) => json(route, {
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/cli/usage**', (route) => json(route, { at: '2026-09-06T08:00:00Z', sessions: [] }));
  await page.route('**/api/cli/quota**', (route) => json(route, {
    at: '2026-09-06T08:00:00Z', ttlSeconds: 600, snapshots: [],
  }));
  await page.route(/\/api\/runner\/status(\?|$)/, (route) => json(route, {
    projects: {
      [PROJECT]: {
        projectName: PROJECT,
        mode: 'manual',
        activeJobId: null,
        activeExecution: null,
        queuedJobIds: [],
      },
    },
  }));
}

async function openBoard(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
    const hideFixtureNoise = () => document.querySelectorAll('app-error-dialog, app-offline-banner')
      .forEach((element) => ((element as HTMLElement).style.display = 'none'));
    addEventListener('DOMContentLoaded', () => {
      hideFixtureNoise();
      new MutationObserver(hideFixtureNoise).observe(document.body, { childList: true, subtree: true });
    }, { once: true });
  });
  await installRoutes(page);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('task-card').first()).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
}

function card(page: Page, title: string) {
  return page.getByTestId('task-card').filter({ hasText: title });
}

test.describe('AGT-2749 human-review failure class', () => {
  test('states the class and the retry counter for a non-product failure only', async ({ page }) => {
    await openBoard(page);

    // Every fixture card is in the Human Review lane.
    await expect
      .poll(async () => page.getByTestId('task-card').count(), { timeout: 15_000 })
      .toBe(HUMAN_REVIEW.length);

    // 1. Infrastructure: the class AND the bounded retry counter are readable
    //    in the lane, without opening the card.
    const infra = card(page, INFRA_TITLE).getByTestId('task-card-failure-class');
    await expect(infra).toHaveCount(1);
    await expect(infra).toContainText('Infrastructure');
    await expect(infra).toContainText('retry 2/3');
    await expect(infra).toHaveAttribute('data-failure-class', 'infrastructure');
    await expect(infra).toHaveAttribute('data-retry-attempt', '2');
    await expect(infra).toHaveAttribute('data-retry-budget', '3');

    // 2. Quota reads the same way at the end of its budget.
    const quota = card(page, QUOTA_TITLE).getByTestId('task-card-failure-class');
    await expect(quota).toContainText('Quota');
    await expect(quota).toContainText('retry 3/3');
    await expect(quota).toHaveAttribute('data-failure-class', 'quota');

    // 3. A product failure and a pre-taxonomy card stay quiet.
    await expect(card(page, PRODUCT_TITLE).getByTestId('task-card-failure-class')).toHaveCount(0);
    await expect(card(page, LEGACY_TITLE).getByTestId('task-card-failure-class')).toHaveCount(0);
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`captures the human-review lane in ${theme} theme`, async ({ page }, testInfo) => {
      await openBoard(page);
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);

      const infra = card(page, INFRA_TITLE).getByTestId('task-card-failure-class');
      await expect(infra).toContainText('Infrastructure');
      await expect(infra).toContainText('retry 2/3');
      await infra.scrollIntoViewIfNeeded();

      const shot = await page.screenshot({ fullPage: false });
      await saveShot(testInfo, `human-review-failure-class-${theme}--mocked.png`, shot);
    });
  }
});
