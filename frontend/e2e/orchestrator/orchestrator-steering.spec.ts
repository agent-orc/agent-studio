import { test, expect } from '../fixtures/dev-backend';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { createJob, getJobDetail } from '../helpers/jobs';

/**
 * Orchestrator steering — when the orchestrator cannot pick a path on its
 * own AND identifies a concrete unblocking ask (a screenshot, a choice
 * between options, a missing doc), it now hands back a structured STEER
 * message instead of opaque `BLOCK`. The chat row renders distinctly:
 * question-mark icon, Need / Why labels, option buttons that pre-fill the
 * compose box, and a "Send screenshot" affordance when the Need mentions
 * a screenshot.
 *
 * Both assertions in this spec are filesystem-fixture-driven: we plant a
 * pre-cooked `cli-output.log` containing the steer message and then visit
 * the job. No live CLI; no quota burned.
 */

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

function copyResultsArtifact(absoluteSourcePath: string, jobFolder: string, name: string): void {
  try {
    if (!fs.existsSync(absoluteSourcePath)) return;
    const dst = path.join(jobFolder, 'results');
    fs.mkdirSync(dst, { recursive: true });
    fs.copyFileSync(absoluteSourcePath, path.join(dst, name));
  } catch {
    /* best-effort: results-folder mirroring failure must not fail the spec */
  }
  // Mirror the same artifact into the task processor's canonical job
  // results folder when the spec is driven by an agent task run. The
  // env var is set by the task processor when it spawns Playwright.
  const taskResults = process.env.JOB_RESULTS_DIR;
  if (taskResults) {
    try {
      fs.mkdirSync(taskResults, { recursive: true });
      fs.copyFileSync(absoluteSourcePath, path.join(taskResults, name));
    } catch {
      /* best-effort */
    }
  }
}

function buildSteerLog(): string {
  // Mirrors the persistence shape OrchestratorChatLog.AppendWithStream
  // writes: `[HH:mm:ss.fff] [stream] body`. We seed an agent line, a
  // [[TASK_NEEDS_INPUT]] marker so the chat reads in order, and the
  // orchestrator's STEER handoff in the exact format
  // `OrchestratorReplyParser.FormatSteerForChat` emits.
  const lines = [
    '[09:30:00.000] [stdout] Working on the steering task',
    '[09:30:30.000] [stdout] [[TASK_NEEDS_INPUT: should I keep the legacy modal or migrate to the side sheet?]]',
    '[09:30:45.123] [orchestrator] [steer] [orchestrator] **Need:** screenshot of the affected modal in light theme  **Why:** I cannot tell which variant is broken without seeing the rendered surface  **Options:** A) keep the legacy modal | B) migrate to the side sheet | C) split the difference and side-load the new one'
  ];
  return lines.join('\n') + '\n';
}

test.describe('Orchestrator steering', () => {
  test('renders steer card with Need/Why/Options and wires option clicks + screenshot affordance', async ({ page, devBackend }, testInfo) => {
    // The dev-backend fixture brings dev up if it is offline, so the
    // spec runs without any pre-arranged state. `devBackend` is unused
    // beyond ensuring the lifecycle ran.
    void devBackend;
    const wp = await pickWatchPath();
    const created = await createJob({
      title: `e2e-orchestrator-steering-${Date.now()}`,
      watchPath: wp.path,
      promptMarkdown: 'Pretend NEEDS_INPUT happened so the orchestrator can hand back a steer card.',
      targetState: '3-progress'
    });
    const detail = await getJobDetail(created.id, wp.path);
    const folder = detail.info.folderPath;

    // Plant the steer line in cli-output.log so the activity-log view
    // renders it without spawning a real CLI.
    const logsDir = path.join(folder, 'logs');
    fs.mkdirSync(logsDir, { recursive: true });
    fs.writeFileSync(path.join(logsDir, 'cli-output.log'), buildSteerLog(), 'utf-8');

    // Visit the job and switch to the Activity tab. The Activity tab is
    // already selected by default once the detail panel opens; click it
    // anyway with `force: true` because the inspector header re-renders
    // mid-load and a "stable" wait flakes against background polling.
    // Steer card lives in the LEGACY activity-log-view's conversation mode;
    // pin Frontend:NextGenChat off ('0') now that it defaults ON so the
    // mode toggle and steer card still render.
    await page.addInitScript(() => localStorage.setItem('atp.flag.nextGenChat', '0'));
    await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);
    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click({ force: true });

    const conversationBtn = page.getByTestId('activity-log-mode-conversation');
    await expect(conversationBtn).toBeVisible({ timeout: 10_000 });
    await conversationBtn.click({ force: true });

    // Steer card is present with its warning status indicator and the parsed
    // Need / Why fields. The first cli-output poll fires ~1 s after the
    // detail panel mounts, then every 5 s while the job is idle. Give the
    // card up to 20 s so a slow first poll never flakes the assertion.
    const card = page.getByTestId('orchestrator-steer-card');
    await expect(card).toBeVisible({ timeout: 20_000 });
    await expect(card.getByTestId('orchestrator-steer-state')).toHaveAttribute('data-tone', 'warning');

    const need = page.getByTestId('orchestrator-steer-need');
    await expect(need).toContainText('screenshot of the affected modal');

    const why = page.getByTestId('orchestrator-steer-why');
    await expect(why).toContainText('cannot tell which variant');

    // Capture a "before" screenshot of the steer card while the compose box
    // is still empty. Saved under results/ so reviewers can inspect it.
    // Scroll the card into view first - the activity panel scrolls
    // independently and the card may sit below the auto-eval banner.
    await card.scrollIntoViewIfNeeded();
    const beforePath = testInfo.outputPath('orchestrator-steer-before.png');
    await card.screenshot({ path: beforePath });
    copyResultsArtifact(beforePath, folder, 'orchestrator-steer-before.png');

    // Options render as buttons. Clicking one pre-fills the compose box.
    const optionButtons = page.getByTestId('orchestrator-steer-options').locator('button');
    await expect(optionButtons).toHaveCount(3);
    await expect(optionButtons.nth(0)).toContainText('keep the legacy modal');
    await expect(optionButtons.nth(1)).toContainText('migrate to the side sheet');

    // dispatchEvent('click') instead of .click(): in this layout the
    // run-timeline empty banner above the activity log and the auto-eval
    // banner below it stack higher than the steer card in pointer-events
    // hit-testing. A coordinate-based click (even with `force: true`)
    // hits the overlying element and the (click) handler never fires.
    // dispatchEvent goes straight to the target element, which is what
    // we want here - we're testing the click-to-prefill contract, not
    // hit-test geometry.
    await optionButtons.nth(1).dispatchEvent('click');

    // Wait for change-detection to flush. The compose <textarea> binds via
    // `[value]="followupPrompt()"`, so the parent signal needs to propagate
    // through Angular's microtask queue before the DOM property updates.
    const composeInput = page.getByTestId('activity-chat-input');
    await expect(composeInput).toHaveValue('migrate to the side sheet', { timeout: 5_000 });

    // Capture the "after option click" state. We screenshot the steer card
    // and the chat compose strip stacked, so the prefilled textarea is
    // visible right next to the option that produced it.
    await card.scrollIntoViewIfNeeded();
    const afterPath = testInfo.outputPath('orchestrator-steer-after-option.png');
    await page.screenshot({ path: afterPath, fullPage: false });
    copyResultsArtifact(afterPath, folder, 'orchestrator-steer-after-option.png');

    // The Need mentions "screenshot", so the upload affordance must be
    // present. Clicking it triggers the hidden file input the parent
    // wires to /api/tasks/{id}/attachments. We assert the affordance
    // exists and click it without supplying a file - we are testing the
    // wiring, not the upload pipe.
    const uploadBtn = page.getByTestId('orchestrator-steer-upload');
    await expect(uploadBtn).toBeVisible();
    const fileInput = page.getByTestId('orchestrator-steer-upload-input');
    await expect(fileInput).toBeAttached();

    // Register a flag we can read back after the user gesture. The click
    // event on the hidden <input type=file> is the bridge between the
    // steer-card affordance and the existing attachment endpoint; the
    // browser is allowed to fire .click() on the input synchronously
    // during the button's user-gesture handler. Asserting the flag flips
    // proves the wiring without exercising the actual upload pipe.
    await page.evaluate(() => {
      (window as unknown as { __steerInputClicked?: boolean }).__steerInputClicked = false;
      const input = document.querySelector('[data-testid="orchestrator-steer-upload-input"]') as HTMLInputElement | null;
      if (!input) return;
      input.addEventListener('click', (e) => {
        (window as unknown as { __steerInputClicked?: boolean }).__steerInputClicked = true;
        // Suppress the native file picker so the test does not stall
        // waiting on a chooser dialog.
        e.preventDefault();
      });
    });
    // dispatchEvent('click') for the same z-index reason as the option
    // click above: a coordinate-based click in this layout lands on the
    // overlying auto-eval banner, not the steer card's button.
    await uploadBtn.dispatchEvent('click');
    const clicked = await page.evaluate(() => (window as unknown as { __steerInputClicked?: boolean }).__steerInputClicked === true);
    expect(clicked).toBe(true);

    // Cleanup: fixture jobs are filtered out of the default board, but we
    // still delete ours so repeated test runs don't leak fixture folders.
    try {
      await api(`/api/tasks/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(wp.path)}`, { method: 'DELETE' });
    } catch {
      /* best-effort */
    }
  });
});
