import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect, type Locator, type Page } from '@playwright/test';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';
import { createFirstRunProofCard, prepareFirstRunProject, verifyFirstRunGit } from '../helpers/first-run-setup';
import {
  ensureReviewHost, killReviewHost, reviewHostGone, startReviewHost, stopReviewHost, type ReviewHostLaunch,
} from '../helpers/review-host-process';

/**
 * AGT-3845 (operator crisis playthrough AGT-3648, finding S4-F4): a review
 * role on another build graded a card Pass into Human Review. The grade and
 * GET /api/tasks/{id} named the grading release, but the card detail's review
 * resource served reviewProjection null as ready, so the card never said a
 * non-Stable build graded it and offered Accept as if Stable had.
 *
 * On the real backend and runner pair of the `first-run` profile, the review
 * daemon is started again advertising another commit of the same version. The
 * proof card is reviewed by it, and without any manual refresh:
 *  - the card detail shows the grading-release notice of GET /api/tasks/{id},
 *  - the Human Review card says so next to Accept and offers a fresh review,
 *  - Execution Hosts lists the card with a link to it.
 */

const DRIFTED_COMMIT = 'b9a3a90d6895';
const DRIFTED_RELEASE = `20261105T1059Z-review-${DRIFTED_COMMIT}`;

const CLIENT = { 'X-Client-Id': 'local-default' };

async function hostReleases<T>(page: Page): Promise<T> {
  const response = await page.request.get('/api/v1/management/host-releases', { headers: CLIENT });
  expect(response.ok(), `host-releases ${response.status()}: ${await response.text()}`).toBeTruthy();
  return await response.json() as T;
}

interface ReviewAttempt { plane: string; workerReleaseStable?: string | null }
interface CardDetail { info: { key?: string | null; state: string; reviewProjection?: { attempts?: ReviewAttempt[] } | null } }

async function gradedNotice(page: Page, cardId: string): Promise<string | null> {
  const response = await page.request.get(`/api/tasks/${encodeURIComponent(cardId)}`, { headers: CLIENT });
  if (!response.ok()) return null;
  const detail = await response.json() as CardDetail;
  if (detail.info.state !== '5-human-review') return null;
  const latest = detail.info.reviewProjection?.attempts?.find(attempt => attempt.plane.toLowerCase() === 'remote');
  return latest?.workerReleaseStable ?? null;
}

/** The card detail's review resource, as the browser reads it for the current core generation. */
async function reviewResource(page: Page, cardId: string, projectName: string):
  Promise<{ state: string; reason: string | null; notice: string | null; lane: string }> {
  const projects = await (await page.request.get('/api/projects', { headers: CLIENT })).json() as
    { id: string; displayName: string }[];
  const project = projects.find(entry => entry.displayName === projectName)!.id;
  const core = await (await page.request.get(
    `/api/tasks/${encodeURIComponent(cardId)}/core?project=${project}`, { headers: CLIENT })).json() as
    { coreVersion: string; lane: string };
  const response = await page.request.get(`/api/tasks/${encodeURIComponent(cardId)}/details/review`
    + `?project=${project}&generation=${core.coreVersion}`, { headers: CLIENT });
  expect(response.ok(), `review resource ${response.status()}: ${await response.text()}`).toBeTruthy();
  const body = await response.json() as { state: string; reason: string | null;
    data: { reviewProjection?: { attempts?: ReviewAttempt[] } | null } };
  const latest = body.data.reviewProjection?.attempts?.find(attempt => attempt.plane.toLowerCase() === 'remote');
  return { state: body.state, reason: body.reason, notice: latest?.workerReleaseStable ?? null, lane: core.lane };
}

async function shots(page: Page, target: Locator, results: string | undefined, name: string): Promise<void> {
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
    for (const theme of ['light', 'dark'] as Theme[]) {
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);
      await target.scrollIntoViewIfNeeded().catch(() => undefined);
      if (results) await page.screenshot({ path: join(results, `${name}-${width}-${theme}.png`), animations: 'disabled' });
    }
  }
  await page.setViewportSize({ width: 1280, height: 900 });
  await setTheme(page, 'light');
}

test('a card graded by a review role off Stable says so on the card and in Execution Hosts', async ({ page }) => {
  test.setTimeout(30 * 60_000);
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace).toBeTruthy();
  const results = process.env.JOB_RESULTS_DIR ? join(process.env.JOB_RESULTS_DIR, 'off-stable-grade') : undefined;
  if (results) mkdirSync(results, { recursive: true });
  let drifted: number | null = null;
  let original: ReviewHostLaunch | null = null;
  try {
    ensureReviewHost(workspace!);
    const projectName = await prepareFirstRunProject(page, workspace!, 'Off Stable Grade');
    const stable = await hostReleases<{ stable: { version: string; commit?: string | null } }>(page);
    expect(stable.stable.commit, 'the e2e backend must know its Stable commit to compare a grading host')
      .toMatch(/^[0-9a-f]{7,40}$/i);
    expect(stable.stable.commit!.startsWith(DRIFTED_COMMIT.slice(0, 7))).toBe(false);

    // 1. The review role restarts on the Stable version but another commit.
    original = killReviewHost(workspace!);
    await expect.poll(() => reviewHostGone(original!), { timeout: 30_000 }).toBe(true);
    drifted = startReviewHost({ ...original, env: { ...original.env,
      RUNNER_RELEASE_ID: DRIFTED_RELEASE, RUNNER_RELEASE_COMMIT: DRIFTED_COMMIT } }, workspace!);
    await expect.poll(async () => {
      const snapshot = await hostReleases<{ hosts: { runnerId: string; state: string }[] }>(page);
      return snapshot.hosts.find(host => host.runnerId === 'first-run-review')?.state;
    }, { timeout: 120_000, intervals: [2_000] }).toBe('differs');

    await page.goto('/#/workspace/settings/first-run');
    await expect(page.getByTestId('first-run-git-setup')).toBeVisible();
    if (await page.getByTestId('first-run-git').getAttribute('data-state') !== 'ready') await verifyFirstRunGit(page);
    await expect(page.getByTestId('first-run-providers')).toHaveAttribute('data-state', 'ready', { timeout: 120_000 });
    await expect(page.getByTestId('first-run-host')).toHaveAttribute('data-state', 'ready');
    const cardId = await createFirstRunProofCard(page);

    // 2. Open the card while it is still in review: the detail must follow the
    // grade into Human Review on its own.
    await page.goto(`/#/tasks/${encodeURIComponent(cardId)}`);
    let notice: string | null = null;
    await expect.poll(async () => (notice = await gradedNotice(page, cardId)),
      { timeout: 20 * 60_000, intervals: [5_000] }).toContain(`commit ${DRIFTED_COMMIT.slice(0, 7)}`);
    expect(notice).toContain('which differs from Stable');
    // The card detail's review resource serves the same projection, ready.
    await expect.poll(async () => JSON.stringify(await reviewResource(page, cardId, projectName)),
      { timeout: 120_000, intervals: [2_000] })
      .toBe(JSON.stringify({ state: 'ready', reason: null, notice, lane: '5-human-review' }));

    // 3. The card detail shows the same sentence GET /api/tasks/{id} returns, without a reload.
    const resultNotice = page.getByTestId('result-grading-release-notice');
    await expect(resultNotice).toContainText(notice!, { timeout: 60_000 });
    const nextToAccept = page.getByTestId('off-stable-grade-notice');
    await expect(nextToAccept).toBeVisible({ timeout: 60_000 });
    await expect(page.getByTestId('off-stable-grade-reason')).toHaveText(notice!);
    await expect(page.getByTestId('off-stable-grade-review-again')).toBeEnabled();
    await shots(page, resultNotice, results, 'card-result-notice');
    await shots(page, nextToAccept, results, 'card-human-review-notice');

    // 4. Execution Hosts lists the card graded off Stable, with a link to it.
    await page.goto('/#/workspace/settings/environment');
    const list = page.getByTestId('off-stable-grades');
    await expect(list).toBeVisible({ timeout: 60_000 });
    // The list keys each row by the card's public key, as its link does.
    const detail = await (await page.request.get(`/api/tasks/${encodeURIComponent(cardId)}`, { headers: CLIENT }))
      .json() as CardDetail;
    const cardKey = detail.info.key ?? cardId;
    const row = list.locator(`[data-testid="off-stable-grade-row"][data-task-key="${cardKey}"]`);
    await expect(row).toHaveCount(1);
    await expect(row.getByTestId('off-stable-grade-notice-text')).toHaveText(notice!);
    await shots(page, list, results, 'execution-hosts-off-stable-grades');
    await row.getByTestId('off-stable-grade-card').click();
    await expect(page).toHaveURL(/#\/tasks\//);
    await expect(page.getByTestId('off-stable-grade-notice')).toBeVisible({ timeout: 60_000 });
  } finally {
    // Leave the profile's review host on its own release for the next spec.
    stopReviewHost(drifted);
    if (original) {
      const launch = original;
      await expect.poll(() => reviewHostGone({ ...launch, killed: drifted ? [drifted] : [] }),
        { timeout: 30_000 }).toBe(true);
      startReviewHost(launch, workspace!);
    }
  }
});
