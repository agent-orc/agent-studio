import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { listJobs } from '../helpers/jobs';

/**
 * Interactive chat continuation — Send button must visibly continue a task.
 *
 * Regression guard for the "Send button does nothing" UX bug: when a Claude
 * task pauses (CLI exited cleanly while waiting for the user) the follow-up
 * sent via the activity-tab compose strip used to disappear with no on-screen
 * confirmation until the next 2 s poll caught the agent's reply.
 *
 * The fix has two halves:
 *   - Backend appends the user's prompt to cli-output.log as a `[user]`-stream
 *     line before resuming the CLI (TaskRunnerService.AppendUserPromptToCliLog).
 *   - Frontend optimistically echoes the same line into the activity log so
 *     it appears immediately on Send, before any HTTP round-trip
 *     (CliOutputPollService.appendOptimisticUserMessage).
 *
 * This spec exercises both — with the optimistic injection disabled the test
 * would still pass (after a 2 s wait), but the *immediate* visibility check
 * after `send.click()` would fail without it.
 */

interface ContinuableJob {
  id: string;
  watchPath: string;
}

/**
 * Find a Claude job with a captured session UUID — the only kind /continue can
 * resume. We deliberately target jobs that have already been started (so they
 * have a session) and cli-output.log content (so the activity tab is non-empty).
 */
async function findContinuableJob(): Promise<ContinuableJob | null> {
  const jobs = await listJobs();
  for (const j of jobs) {
    const detail = await api<{ info: { sessionName: string | null; cliType: string | null } }>(
      `/api/tasks/${encodeURIComponent(j.id)}?watchPath=${encodeURIComponent(j.watchPath)}`
    );
    const session = detail.info.sessionName;
    const cli = detail.info.cliType;
    if (cli === 'claude' && session && /^[0-9a-f-]{36}$/i.test(session)) {
      const out = await api<unknown[]>(
        `/api/tasks/${encodeURIComponent(j.id)}/output?watchPath=${encodeURIComponent(j.watchPath)}`
      );
      if (Array.isArray(out) && out.length > 0) return { id: j.id, watchPath: j.watchPath };
    }
  }
  return null;
}

async function openJobDetail(page: Page, target: ContinuableJob): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
  await expect(page.getByTestId('inspector-tab-protocol')).toBeVisible({ timeout: 15_000 });

  // The detail view auto-opens an error dialog whenever the last execution
  // snapshot is "failed" — earlier tests in the run can leave such a snapshot.
  // Retry dismissal for ~2 s in case the dialog opens slightly later than the
  // detail fade-in.
  const deadline = Date.now() + 2000;
  while (Date.now() < deadline) {
    const closeBtn = page.locator('.error-dialog .error-dialog__close').first();
    if (!(await closeBtn.isVisible().catch(() => false))) break;
    await closeBtn.click({ force: true }).catch(() => undefined);
    await page.waitForTimeout(150);
  }
}

async function stopAnyLiveRun(target: ContinuableJob): Promise<void> {
  await fetch(
    `${BACKEND}/api/tasks/${encodeURIComponent(target.id)}/stop?watchPath=${encodeURIComponent(target.watchPath)}`,
    { method: 'POST' }
  ).catch(() => undefined);
}

test.describe('Activity tab — interactive chat continuation', () => {
  // We stub /continue at the network layer rather than letting it spawn a real
  // Claude run. The bug under test is purely frontend (optimistic echo + the
  // backend-written [user] log line is verified separately). Stubbing keeps
  // the tests fast (<2 s each), avoids burning quota, and stops the previous
  // test from leaving the runner in a "failed" snapshot that re-opens the
  // error dialog on the next page load and blocks pointer events.
  test.beforeEach(async ({ page }) => {
    await page.route('**/api/v1/projects/*/tasks/*/continue**', async route => {
      const body = JSON.stringify({
        status: 'started',
        execution: {
          jobId: 'stub',
          jobKey: 'stub',
          processId: 0,
          startedAt: new Date().toISOString(),
          status: 'running',
          exitCode: null,
          durationSeconds: null,
          model: 'claude-opus-4-7'
        }
      });
      await route.fulfill({ status: 200, contentType: 'application/json', body });
    });
  });

  test('Send echoes the user message into the activity log immediately and posts /continue', async ({ page }) => {
    const target = await findContinuableJob();
    if (!target) {
      test.skip(true, 'No claude job with session + output available; cannot test continuation.');
      return;
    }

    await openJobDetail(page, target);
    await page.getByTestId('inspector-tab-activity').click();

    const input = page.getByTestId('activity-chat-input');
    const send = page.getByTestId('activity-chat-send');
    await expect(input).toBeVisible();
    await expect(send).toBeVisible();
    await expect(send).toBeDisabled();

    const followup = `e2e-chat-continue probe ${Date.now()}`;
    await input.fill(followup);
    await expect(send).toBeEnabled();

    // Click Send and capture the /continue response. Don't assert on the body
    // here — failures with a clear message (e.g. quota) should still let the
    // optimistic-echo assertion below verify the UX path.
    const responsePromise = page.waitForResponse(
      r => r.url().includes('/continue') && r.request().method() === 'POST',
      { timeout: 8000 }
    );
    await send.click();

    // Optimistic echo: the user's message must appear in the activity log
    // *immediately* - before the response lands and before the next 2 s poll.
    // The default Activity tab mode is "Conversation"; user turns are
    // rendered with data-testid="convo-turn-user".
    const userTurn = page.getByTestId('convo-turn-user').last();
    await expect(userTurn).toBeVisible({ timeout: 1000 });
    await expect(userTurn).toContainText(followup);

    // Input clears on send.
    await expect(input).toHaveValue('');

    const response = await responsePromise;
    expect(response.status(), 'POST /continue should succeed').toBe(200);

    // A live run still has the same single Send action. Its title explains
    // that the safe flow pauses the current run before the next message.
    await expect(send).toHaveText('Send', { timeout: 5000 });
    await expect(send).toHaveAttribute('title', /Pause the current run/i);

    await page.screenshot({ path: 'test-results/chat-continue-after-send.png', fullPage: false });

    // Chat-mode rendering of the user role is unit-tested in
    // activity-log.parser.spec.ts (buildChatMessages with stream='user'). We
    // skip that DOM assertion here because the embedded activity log can
    // render very small in the protocol pane and the click may not land
    // reliably without a layout test fixture.

    await stopAnyLiveRun(target);
  });

  test('Send is disabled when the input is empty', async ({ page }) => {
    const target = await findContinuableJob();
    if (!target) {
      test.skip(true, 'No claude job with session + output available.');
      return;
    }

    await openJobDetail(page, target);
    await page.getByTestId('inspector-tab-activity').click();

    const input = page.getByTestId('activity-chat-input');
    const send = page.getByTestId('activity-chat-send');
    await expect(send).toBeDisabled();

    await input.fill('hello');
    await expect(send).toBeEnabled();

    await input.fill('');
    await expect(send).toBeDisabled();

    await input.fill('   \n\t  '); // whitespace-only also counts as empty
    await expect(send).toBeDisabled();
  });

});

/**
 * Backend persistence — exercised through the API directly so we don't have
 * to drive the UI through a real Claude resume (slow, billable, and leaves a
 * failed-status snapshot that re-opens the error dialog on the next test).
 *
 * The contract: POST /continue must record the user's prompt as a `[user]`
 * stream line in cli-output.log so subsequent reloads of the activity tab
 * show the message even after the optimistic in-memory echo is gone.
 */
test.describe('Continue persists user prompt to cli-output.log', () => {
  test('GET /output returns the user follow-up as a [user]-stream line', async () => {
    const target = await findContinuableJob();
    if (!target) {
      test.skip(true, 'No claude job with session + output available.');
      return;
    }

    const followup = `e2e-persist probe ${Date.now()}`;

    // Snapshot BEFORE so we can assert this exact follow-up was added.
    const before = await api<{ stream: string; text: string }[]>(
      `/api/tasks/${encodeURIComponent(target.id)}/output?watchPath=${encodeURIComponent(target.watchPath)}`
    );
    const beforeMatches = before.filter(l => l.stream === 'user' && l.text.includes(followup));
    expect(beforeMatches).toHaveLength(0);

    await api(
      `/api/tasks/${encodeURIComponent(target.id)}/continue?watchPath=${encodeURIComponent(target.watchPath)}`,
      { method: 'POST', body: JSON.stringify({ prompt: followup }) }
    );

    // Stop immediately — we only care that the [user] line was persisted, not
    // that the agent finishes.
    await stopAnyLiveRun(target);

    const after = await api<{ stream: string; text: string }[]>(
      `/api/tasks/${encodeURIComponent(target.id)}/output?watchPath=${encodeURIComponent(target.watchPath)}`
    );
    const afterMatches = after.filter(l => l.stream === 'user' && l.text.includes(followup));
    expect(afterMatches.length, 'cli-output.log must contain the user follow-up as a [user] line').toBeGreaterThanOrEqual(1);
  });
});
