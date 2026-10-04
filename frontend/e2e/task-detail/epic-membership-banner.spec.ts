import { expect, Page } from '@playwright/test';
import * as path from 'path';
import { test } from '../fixtures/dev-backend';
import { api, BACKEND } from '../helpers/api';

/**
 * Task-detail epic-membership banner + parent-epic open request.
 *
 * When the open card is a sub-task of an epic (epicId set, kind != epic) the
 * detail view shows a clickable banner under the header with the epic's
 * key + title; clicking it asks the app to open the epic. Epics themselves and
 * epic-less tasks show no banner.
 *
 * The spec drives the live frontend (proxied to a real backend): it creates a
 * temporary epic plus sub-task through the task API, then pins the one piece
 * the banner keys off - the epic rollup (GET /api/epics/{id}) - so the key chip
 * is deterministic. Navigation and the detail payloads come from the backend
 * the frontend proxies to.
 *
 * Screenshots land under JOB_RESULTS_DIR/epic-membership when the orchestrator
 * sets it (or EPIC_MEMBERSHIP_SHOTS), else test-results/ (scratch).
 */

const SHOTS_DIR = process.env.EPIC_MEMBERSHIP_SHOTS?.trim()
  || (process.env.JOB_RESULTS_DIR
    ? path.join(process.env.JOB_RESULTS_DIR, 'epic-membership')
    : path.resolve(__dirname, '../../test-results/epic-membership'));

const MOCK_KEY = 'ASS-597';
const MOCK_TITLE = 'EPIC: Epics-Feature Ausbau';
const PREFIX = 'e2e-epic-membership-';

interface WatchPath { name: string; path: string; rootPath: string; }
interface TaskLite { id: string; watchPath: string; }

async function getTestWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths.find(p => p.name === 'Playwright Test') ?? paths[0];
}

async function listTasks(): Promise<TaskLite[]> {
  return api<TaskLite[]>('/api/tasks?includeFixtures=true');
}

async function createTask(input: {
  id: string;
  title: string;
  watchPath: string;
  kind?: string;
  epicId?: string;
}): Promise<string> {
  const res = await api<{ id: string }>('/api/tasks', {
    method: 'POST',
    body: JSON.stringify({
      id: input.id,
      title: input.title,
      watchPath: input.watchPath,
      agent: 'claude',
      cliType: 'claude',
      model: null,
      promptMarkdown: null,
      targetState: '5-human-review',
      kind: input.kind ?? 'task',
      epicId: input.epicId ?? null,
      fixture: false,
    }),
  });
  return res.id;
}

async function deleteTask(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
    headers: { 'x-client-id': process.env.PW_CLIENT_ID || 'local-default' },
  });
}

async function cleanup(): Promise<void> {
  const all = await listTasks();
  const stale = all.filter(j => j.id.startsWith(PREFIX));
  await Promise.all(stale.map(j => deleteTask(j.id, j.watchPath).catch(() => undefined)));
}

async function mockEpic(
  page: Page,
  epicId: string,
  watchPath: string,
  onRequest: () => void = () => undefined,
): Promise<void> {
  const body = JSON.stringify({
    id: epicId, key: MOCK_KEY, title: MOCK_TITLE, projectName: 'agent-taskboard',
    watchPath, state: '2-ready', subTaskTotal: 0, completed: 0, inProgress: 0, open: 0,
    byState: {}, subTasks: [],
  });
  await page.route(`**/api/epics/${encodeURIComponent(epicId)}**`, (route) => {
    onRequest();
    return route.fulfill({ status: 200, contentType: 'application/json', body });
  });
}

async function mockEpicDetail(page: Page, epicId: string, watchPath: string, onRequest: (url: URL) => void): Promise<void> {
  const body = JSON.stringify({
    info: {
      id: epicId,
      taskKey: `${watchPath}::${epicId}`,
      title: MOCK_TITLE,
      state: '2-ready',
      agent: 'claude',
      cliType: 'claude',
      model: null,
      watchPath,
      projectName: 'agent-taskboard',
      folderPath: '',
      execution: null,
      kind: 'epic',
      epicId: null,
    },
    promptMarkdown: '# Mock epic',
    statusMarkdown: null,
    log: [],
  });
  await page.route(`**/api/v1/projects/*/tasks/${encodeURIComponent(epicId)}**`, (route) => {
    const url = new URL(route.request().url());
    if (!/^\/api\/v1\/projects\/[^/]+\/tasks\/[^/]+$/.test(url.pathname)
      || !url.pathname.endsWith(`/tasks/${encodeURIComponent(epicId)}`)) {
      return route.fallback();
    }
    onRequest(url);
    return route.fulfill({ status: 200, contentType: 'application/json', body });
  });
}

async function openTask(page: Page, t: TaskLite): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(
    `/?job=${encodeURIComponent(t.id)}&watchPath=${encodeURIComponent(t.watchPath)}`,
    { waitUntil: 'domcontentloaded', timeout: 30_000 },
  );
  await dismissCrashRecovery(page);
}

async function dismissCrashRecovery(page: Page): Promise<void> {
  const recoveryOverlay = page.getByTestId('crash-recovery-prompt-overlay');
  if (await recoveryOverlay.isVisible().catch(() => false)) {
    await page.getByTestId('crash-recovery-dismiss-all').click();
    await expect(recoveryOverlay).toBeHidden({ timeout: 10_000 });
  }
}

test.describe('Task-detail epic-membership banner', () => {
  test.beforeEach(() => test.setTimeout(120_000));
  test.afterEach(() => cleanup());

  test('sub-task shows its epic as a flat band and requests the parent epic on click', async ({ page, devBackend: _devBackend }) => {
    const wp = await getTestWatchPath();
    await cleanup();
    const epicId = await createTask({
      id: `${PREFIX}epic`,
      title: MOCK_TITLE,
      watchPath: wp.path,
      kind: 'epic',
    });
    const sub: TaskLite = {
      id: await createTask({
        id: `${PREFIX}sub`,
        title: `${PREFIX}sub-task`,
        watchPath: wp.path,
        epicId,
      }),
      watchPath: wp.path,
    };
    const epicDetailRequestUrls: URL[] = [];
    let epicRollupRequests = 0;
    await mockEpic(page, epicId, sub.watchPath, () => epicRollupRequests++);
    await mockEpicDetail(page, epicId, sub.watchPath, (url) => epicDetailRequestUrls.push(url));
    await openTask(page, sub);

    const banner = page.getByTestId('epic-membership-banner');
    await expect(banner).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('epic-membership-key')).toHaveText(MOCK_KEY);
    await expect(page.getByTestId('epic-membership-title')).toContainText('Epics-Feature Ausbau');
    const chrome = await banner.evaluate(el => {
      const bannerStyle = getComputedStyle(el);
      const key = el.querySelector('[data-testid="epic-membership-key"]');
      const keyStyle = key ? getComputedStyle(key) : null;
      return {
        background: bannerStyle.backgroundColor,
        borderTopWidth: bannerStyle.borderTopWidth,
        borderRadius: bannerStyle.borderRadius,
        keyBorderTopWidth: keyStyle?.borderTopWidth ?? null,
      };
    });
    expect(chrome.background).not.toBe('rgba(0, 0, 0, 0)');
    expect(chrome.borderTopWidth).toBe('0px');
    expect(chrome.borderRadius).toBe('0px');
    expect(chrome.keyBorderTopWidth).toBe('0px');

    await page.screenshot({ path: path.join(SHOTS_DIR, 'sub-task-epic-banner.png'), fullPage: false });

    // Clicking the flat band resolves through the path-free project handle and
    // retargets the current task editor into the parent Epic tab.
    epicDetailRequestUrls.length = 0;
    epicRollupRequests = 0;
    await dismissCrashRecovery(page);
    await banner.click();
    await expect.poll(() => epicDetailRequestUrls.length, { timeout: 15_000 }).toBeGreaterThan(0);
    expect(epicDetailRequestUrls[0].searchParams.get('project')).toBeTruthy();
    expect(epicDetailRequestUrls[0].searchParams.has('watchPath')).toBe(false);
    await expect.poll(() => epicRollupRequests, { timeout: 15_000 }).toBeGreaterThan(0);
  });

  test('a task without an epic shows no banner', async ({ page, devBackend: _devBackend }) => {
    const wp = await getTestWatchPath();
    await cleanup();
    const plain: TaskLite = {
      id: await createTask({
        id: `${PREFIX}plain`,
        title: `${PREFIX}plain-task`,
        watchPath: wp.path,
      }),
      watchPath: wp.path,
    };
    await openTask(page, plain);
    // The detail view must mount before we can assert the banner is absent.
    // detail-panes is the always-present pane container (the project chip in
    // the header is conditionally hidden, so it is not a reliable readiness cue).
    await expect(page.getByTestId('detail-panes')).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('epic-membership-banner')).toHaveCount(0);
  });
});
