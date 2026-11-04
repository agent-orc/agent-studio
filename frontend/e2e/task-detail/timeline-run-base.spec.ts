import { test, expect, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

/**
 * AGT-3483 (operator-agent-level Dossier, OP-3): an operator continue round
 * records the result ref it starts from on the card timeline. The row names
 * the origin, the ref and the base SHA, and hides the machine reason code.
 *
 * The card is real; only `GET /api/tasks/{id}/timeline` is route-mocked so the
 * row does not depend on a runner claim. Light and dark captures are written
 * to `$JOB_RESULTS_DIR/run-base/` for review.
 */

interface WatchPath { path: string; name?: string }

const BASE_SHA = '3f9c2a7b1d4e5f60718293a4b5c6d7e8f9012345';
const BASE_REF = `refs/heads/agent-studio/results/run_demo_continue/fence-3/${BASE_SHA}`;

const EVENTS = [
  {
    ts: '2026-11-04T08:00:00Z', kind: 'agent_run_finished', actor: 'agent',
    summary: 'codex run completed after 412.0s', details: { cli: 'codex', status: 'completed' },
  },
  {
    ts: '2026-11-04T09:15:00Z', kind: 'continuation_base_recorded', actor: 'human:operator',
    runId: 'run_demo_continue',
    summary: `The steer round starts from the card's latest result ${BASE_REF} at ${BASE_SHA.slice(0, 12)}.`,
    details: {
      ref: BASE_REF, sha: BASE_SHA, reason: 'continue-round-result',
      resultAttemptId: 'run_demo_continue', source: 'operator-continue',
      base: 'latest-result', mode: 'steer',
    },
  },
];

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function seedTimeline(page: Page): Promise<void> {
  await page.route(/\/api\/tasks\/[^/]+\/timeline(\?|$)/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(EVENTS),
  }));
}

function evidencePath(testInfo: TestInfo, name: string): string {
  const root = process.env['JOB_RESULTS_DIR']?.trim()
    ? path.resolve(process.env['JOB_RESULTS_DIR'])
    : testInfo.outputDir;
  const folder = path.join(root, 'run-base');
  fs.mkdirSync(folder, { recursive: true });
  return path.join(folder, name);
}

for (const theme of ['light', 'dark'] as Theme[]) {
  test(`timeline names the run base of a continue round (${theme})`, async ({ page }, testInfo) => {
    const watchPath = await pickWatchPath();
    await seedTimeline(page);
    const { id } = await createJob({
      title: `run-base-timeline-${theme}-${Date.now()}`,
      watchPath,
      promptMarkdown: '# Continue round on a delivered card',
      targetState: '0-backlog',
    });

    try {
      await page.setViewportSize({ width: 1440, height: 1000 });
      await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);
      await page.getByTestId('prompt-tab-timeline').click();

      const row = page.locator('[data-testid="timeline-event"][data-kind="continuation_base_recorded"]');
      await expect(row).toHaveCount(1);
      await expect(row.getByTestId('timeline-event-kind')).toHaveText('Run base · latest result');
      await expect(row.getByTestId('timeline-event-summary')).toContainText(BASE_REF);
      await expect(row).toContainText(`Base SHA${BASE_SHA}`);
      await expect(row.getByTestId('timeline-event-reason')).toHaveCount(0);
      await expect(row).not.toContainText('continue-round-result');
      await expect(row).not.toContainText('operator-continue');

      await row.screenshot({ path: evidencePath(testInfo, `timeline-run-base-${theme}.png`) });
      await page.getByTestId('timeline-list').screenshot({
        path: evidencePath(testInfo, `timeline-run-base-list-${theme}.png`),
      });
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' },
      );
    }
  });
}
