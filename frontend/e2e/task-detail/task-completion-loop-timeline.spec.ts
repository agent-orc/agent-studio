import { test, expect, type Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

/**
 * Completion-loop visible representation (ASS-566 / ADR-0049).
 *
 * The orchestrator retries a task until it is truly done, feeding the
 * specific gap into the next attempt. This spec proves the two operator-
 * facing surfaces that make the loop legible:
 *   - the Overview attempt-cycle strip, consolidated into the Pipeline
 *     section header (Attempt N/M + latest verdict + re-open counter);
 *   - the Timeline tab (prominent final verdict banner + full reopen->retry
 *     ->verdict event story).
 *
 * The `GET /api/tasks/{id}/timeline` endpoint is route-mocked so the test is
 * deterministic and does not depend on a real task having accrued loop
 * activity. The rest of the app (board, task detail bootstrap) is served by
 * the live dev backend per the suite's standing prerequisite.
 */

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

/** Intercept the per-task timeline endpoint and serve a synthetic ledger. */
async function seedTimeline(page: Page, events: unknown[]): Promise<void> {
  await page.route(/\/api\/tasks\/[^/]+\/timeline(\?|$)/, async route => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(events),
    });
  });
}

const RUN_FINISHED = {
  ts: '2026-05-30T10:00:00Z', kind: 'agent_run_finished', actor: 'agent',
  summary: 'Agent claimed the visual-polish work is done',
};
const REOPEN_1 = {
  ts: '2026-05-30T10:05:00Z', kind: 'quality_loop_reopened', actor: 'quality-loop',
  summary: 'Re-opened: change did not land',
  details: { attempt: '2', maxAttempts: '3', gap: 'button still misaligned by 4px' },
};
const REOPEN_2 = {
  ts: '2026-05-30T10:20:00Z', kind: 'quality_loop_reopened', actor: 'quality-loop',
  summary: 'Re-opened again',
  details: { attempt: '3', maxAttempts: '3', gap: 'spacing fixed but colour token still wrong' },
};

test.describe('Task completion loop — Overview indicator + Timeline tab', () => {
  test('escalation path: Overview shows attempt budget exhausted; Timeline pins the escalate verdict', async ({ page }) => {
    const watchPath = await pickWatchPath();
    await seedTimeline(page, [
      RUN_FINISHED,
      REOPEN_1,
      REOPEN_2,
      {
        ts: '2026-05-30T10:40:00Z', kind: 'orchestrator_escalated', actor: 'orchestrator',
        summary: 'Handed to a human — could not reach truly-done in budget',
        details: { attempt: '3', maxAttempts: '3', reason: 'attempt budget exhausted' },
      },
    ]);

    const { id } = await createJob({
      title: `completion-loop-escalated-${Date.now()}`,
      watchPath,
      promptMarkdown: '# Visual polish task that keeps failing the done-check',
      targetState: '4-auto-review',
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);

      // Overview is the default tab — the attempt-cycle indicator must be live.
      const loop = page.getByTestId('overview-completion-loop');
      await expect(loop).toBeVisible({ timeout: 10_000 });
      // Consolidated into the Pipeline section header (no separate block): the
      // loop strip renders inside the Pipeline section, not as its own block.
      await expect(page.getByTestId('overview-pipeline').getByTestId('overview-completion-loop')).toBeVisible();
      await expect(page.getByTestId('overview-loop-verdict')).toHaveAttribute('data-verdict', 'escalated');
      await expect(page.getByTestId('overview-loop-attempt')).toContainText('3 / 3');
      await expect(page.getByTestId('overview-loop-reopens')).toContainText('2');
      await expect(page.getByTestId('overview-loop-reason')).toContainText('attempt budget exhausted');
      await page.screenshot({ path: 'test-results/completion-loop-overview-escalated.png', fullPage: false });

      // Timeline tab: prominent final-verdict banner + the full cycle.
      await page.getByTestId('prompt-tab-timeline').click();
      const banner = page.getByTestId('timeline-verdict-banner');
      await expect(banner).toBeVisible();
      await expect(banner).toHaveAttribute('data-verdict', 'escalated');
      await expect(page.getByTestId('timeline-verdict-label')).toContainText('Escalated to human');
      await expect(page.getByTestId('timeline-event')).toHaveCount(4);
      await page.screenshot({ path: 'test-results/completion-loop-timeline-escalated.png', fullPage: false });
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' },
      );
    }
  });

  test('reopen event exposes the exact steering prompt + context (ASS-734 traceability)', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const steeringPrompt = [
      'STEER THE DIFF, DO NOT RESTART: Do ONLY the open remaining work.',
      '',
      'Commits already made for this task (build on these, do not repeat them):',
      '- a1b2c3 feat: lane move',
      '',
      'Open items:',
      '- [ ] colour token still wrong',
    ].join('\n');
    await seedTimeline(page, [
      RUN_FINISHED,
      {
        ts: '2026-05-30T10:05:00Z', kind: 'quality_loop_reopened', actor: 'quality-loop',
        summary: 'Re-opened: steered the diff, did not restart',
        details: {
          attempt: '2', maxAttempts: '3',
          gap: 'colour token still wrong',
          followUpPrompt: steeringPrompt,
        },
      },
    ]);

    const { id } = await createJob({
      title: `completion-loop-steering-${Date.now()}`,
      watchPath,
      promptMarkdown: '# Task whose reissue carries a traceable steering prompt',
      targetState: '4-auto-review',
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);

      await page.getByTestId('prompt-tab-timeline').click();
      await expect(page.getByTestId('timeline-verdict-banner')).toBeVisible();

      // The reopen event carries an expandable structured steering block
      // (ASS-776) holding the verbatim steer prompt + context the
      // orchestrator handed the agent.
      const steering = page.getByTestId('timeline-event-steering');
      await expect(steering).toBeVisible();
      await expect(steering.locator('summary')).toContainText('Steer prompt + context');
      // Collapsed by default: the verbatim body is not yet rendered visibly.
      await expect(steering.locator('.steer__prompt')).toBeHidden();

      await steering.locator('summary').click();
      await expect(steering.locator('.steer__prompt')).toBeVisible();
      // Expanded: the diff-only rule + the prior-commits block are now visible,
      // proving the operator can confirm the agent was told to steer the diff.
      await expect(steering).toContainText('STEER THE DIFF, DO NOT RESTART');
      await expect(steering).toContainText('Commits already made for this task');
      await expect(steering).toContainText('a1b2c3 feat: lane move');

      await steering.screenshot({
        path: 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/3-progress/fix-orchestrator-nur-das-diff-nachsteuern-statt-neu-von-vorn--nachvollziehbare-steuerungkontext/results/timeline-steering-prompt-expanded.png',
      });
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' },
      );
    }
  });

  test('accepted-after-reopen: Overview + Timeline land on the accept verdict', async ({ page }) => {
    const watchPath = await pickWatchPath();
    await seedTimeline(page, [
      RUN_FINISHED,
      REOPEN_1,
      {
        ts: '2026-05-30T10:30:00Z', kind: 'orchestrator_verdict_accepted', actor: 'orchestrator',
        summary: 'All aspects pass — work is truly done',
      },
    ]);

    const { id } = await createJob({
      title: `completion-loop-accepted-${Date.now()}`,
      watchPath,
      promptMarkdown: '# Task that lands on the second attempt',
      targetState: '4-auto-review',
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const loop = page.getByTestId('overview-completion-loop');
      await expect(loop).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId('overview-loop-verdict')).toHaveAttribute('data-verdict', 'accepted');
      await expect(page.getByTestId('overview-loop-attempt')).toContainText('2');

      await page.getByTestId('prompt-tab-timeline').click();
      const banner = page.getByTestId('timeline-verdict-banner');
      await expect(banner).toHaveAttribute('data-verdict', 'accepted');
      await expect(page.getByTestId('timeline-event')).toHaveCount(3);
      await page.screenshot({ path: 'test-results/completion-loop-timeline-accepted.png', fullPage: false });
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' },
      );
    }
  });
});
