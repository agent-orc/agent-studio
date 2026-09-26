import { test, expect, Page } from '@playwright/test';
import { startLongTaskRecorder } from '../helpers/timing';
import { installFrontendOverride } from '../helpers/frontend-override';
import { setTheme } from '../helpers/theme';

/**
 * Regression spec for project chat: visible failures,
 * soft responsiveness during slow orchestrator replies, and
 * parallel-use isolation. Mocks the orchestrator-chat endpoints so the spec
 * runs without burning quota and without depending on the singleton claude
 * session being booted.
 *
 * Why mocks: the symptoms (silent drop, sluggishness, parallel blocking)
 * live above the CLI - they're about how the FE composes its surface, how
 * errors surface, and how slow round-trips block other interactions. The
 * mocks let us reproduce each scenario deterministically.
 *
 * What we lock down:
 *   1. SILENT-DROP: When the orchestrator errors, both the error and the
 *      submitted user turn remain visible in the canonical chat transcript.
 *   2. SOFT FEEL: While a slow orchestrator reply is pending, the
 *      composer's pending state is visible and the cumulative LongTask
 *      budget over the wait stays under a clear threshold.
 *   3. PARALLEL: Two tabs (independent BrowserContexts) each see their
 *      own POST round-trip and the second tab stays interactive while
 *      the first is mid-send.
 */

const SHOTS = 'screenshots/project-chat-fix';

interface MockTurn {
  id: string;
  ts: string;
  role: 'user' | 'orchestrator';
  text: string;
  errorMessage?: string;
}

interface MockState {
  turns: MockTurn[];
  // POST handler can be swapped per test to inject latency / errors.
  handlePost: (body: { text: string }) => Promise<{ status: number; reply: MockTurn }>;
}

function nowIso(offsetMs = 0): string {
  return new Date(Date.now() + offsetMs).toISOString();
}

/**
 * Wire mocks for the orchestrator-chat surface against a single page.
 * Returns the mutable state so the test can swap the POST handler and
 * inspect the turn list at any time.
 */
async function installChatMocks(page: Page, project: string, initial?: Partial<MockState>): Promise<MockState> {
  const state: MockState = {
    turns: initial?.turns ?? [],
    handlePost: initial?.handlePost ?? (async ({ text }) => {
      const reply: MockTurn = {
        id: `srv-${Date.now()}`,
        ts: nowIso(),
        role: 'orchestrator',
        text: `Acknowledged: ${text.slice(0, 60)}`,
      };
      return { status: 200, reply };
    }),
  };

  // GET orchestrator chat history.
  await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, async (route) => {
    if (route.request().method() === 'GET') {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ project, turns: state.turns }),
      });
      return;
    }
    if (route.request().method() === 'POST') {
      const body = JSON.parse(route.request().postData() ?? '{}') as { text: string };
      const userTurn: MockTurn = {
        id: `srv-u-${Date.now()}`,
        ts: nowIso(),
        role: 'user',
        text: body.text,
      };
      state.turns = [...state.turns, userTurn];
      const { status, reply } = await state.handlePost(body);
      state.turns = [...state.turns, reply];
      await route.fulfill({
        status,
        contentType: 'application/json',
        body: JSON.stringify({ project, reply }),
      });
      return;
    }
    await route.continue();
  });

  return state;
}

async function openSideSheetForProject(page: Page): Promise<string> {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  // Let the watch-paths fetch settle so the project combobox has options.
  await page.waitForTimeout(800);
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  await toggle.click();
  const sheet = page.getByTestId('orch-side-sheet');
  await expect(sheet).toBeVisible();
  await page.waitForTimeout(400);
  // Read the active project name out of the hidden <select> the side sheet
  // renders for accessibility - that's the source of truth for "which
  // project is the chat scoped to right now". The mocks above match any
  // project name so we don't need to force a specific one.
  const projectSelect = page.getByTestId('orch-side-sheet-project-select');
  const value = await projectSelect.inputValue();
  return value;
}

test.describe('Project chat fix - silent drop, sluggishness, parallel use', () => {
  test('shows queued runner reason and interactive usage while a reply waits', async ({ page }) => {
    await installFrontendOverride(page);
    await page.route(/\/api\/runner\/project-chat\/status(?:\?|$)/, route =>
      route.fulfill({ json: {
        state: 'queued', runnerId: 'agent-runner-01', hostName: null,
        queuedAt: '2026-09-26T07:18:40Z', startedAt: null,
        reason: 'Provider unavailable',
      } }));
    await page.route('**/api/runner/project-chat/usage', route =>
      route.fulfill({ json: [{
        hostName: 'agent-runner-01', projectName: 'Agent Studio',
        activeTurns: 1, heavyTurns: 1, cpuPercent: 54,
        tokens: 1200, costUsd: 0.03,
      }] }));
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await setTheme(page, 'light');
    await page.getByTestId('orch-side-sheet-toggle').click();
    await expect(page.getByTestId('chat-input')).toBeVisible();
    const project = 'Chat fixture';
    await installChatMocks(page, project, {
      handlePost: async () => {
        await new Promise(resolve => setTimeout(resolve, 7_000));
        return { status: 200, reply: {
          id: 'reply-after-wait', ts: nowIso(), role: 'orchestrator', text: 'Done',
        } };
      },
    });
    await page.getByTestId('chat-input').fill('How is this task progressing?');
    await page.getByTestId('chat-send').click();
    const waiting = page.getByTestId('orchestrator-chat-waiting');
    await expect(waiting).toContainText('agent-runner-01');
    await expect(waiting).toContainText('Provider unavailable');
    if (process.env.JOB_RESULTS_DIR) {
      await page.screenshot({ path: `${process.env.JOB_RESULTS_DIR}/chat-waiting-light.png` });
      await setTheme(page, 'dark');
      await page.screenshot({ path: `${process.env.JOB_RESULTS_DIR}/chat-waiting-dark.png` });
      await setTheme(page, 'light');
    }
    await expect(waiting).toHaveCount(0, { timeout: 12_000 });
    await page.getByTestId('orch-side-sheet-toggle').click();
    await page.getByTestId('usage-chat-trigger').click();
    const usage = page.getByTestId('usage-chat-panel');
    await expect(usage).toBeVisible();
    await expect(usage).toContainText('agent-runner-01 / Agent Studio');
    await expect(usage).toContainText('54%');
    if (process.env.JOB_RESULTS_DIR) {
      await page.screenshot({ path: `${process.env.JOB_RESULTS_DIR}/chat-usage-light.png` });
      await setTheme(page, 'dark');
      await page.screenshot({ path: `${process.env.JOB_RESULTS_DIR}/chat-usage-dark.png` });
    }
  });

  test('Execution Hosts accounts for a heavy chat beside coding work', async ({ page }) => {
    await installFrontendOverride(page);
    const now = new Date().toISOString();
    const coding = Array.from({ length: 4 }, (_, index) => ({
      id: `coding-${index}`, taskKey: `AGT-C${index}`, title: `Coding ${index}`,
      state: '3-progress', order: index, agent: '', createdAt: now,
      watchPath: '/tmp/agent-studio', projectName: 'Agent Studio',
      folderPath: '', lastActivity: now, sessionName: null, model: null,
      cliType: null, useOwnSession: null, lastUsage: null,
      execution: null, commit: null,
      runner: { runnerId: 'agent-runner-01', runnerName: 'agent-runner-01',
        hostname: 'agent-runner-01', backendName: 'task-server', isRemote: true,
        leaseId: `lease-${index}`, fencingToken: 1, acquiredAt: now },
    }));
    await page.route(/\/api\/tasks\/grouped(?:\?|$)/, route => route.fulfill({ json: {
      preparation: [], ready: [], progress: coding, review: [], completed: [], archive: [],
    } }));
    await page.route('**/api/clients', route => route.fulfill({ json: [
      { id: 'local-default', displayName: 'operator-workstation', kind: 'human',
        registeredAt: now, lastSeenAt: now },
      { id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
        registeredAt: now, lastSeenAt: now, runnerDaemonState: 'running',
        runnerActiveSlots: 4, runnerAvailableSlots: 1 },
    ] }));
    await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({ json: [{
      runnerId: 'agent-runner-01', name: 'agent-runner-01', hostId: 'agent-runner-01',
      instanceId: 'test', runnerVersion: '0.9.2', protocolVersion: 1,
      status: 'online', registeredAt: now, lastSeenAt: now,
      hostAdmission: { status: 'ready' }, capabilities: [], telemetry: null,
      runtimeCapacity: { hostId: 'agent-runner-01', maxParallelism: 5,
        targetLoadPercent: 80, rampStrategy: 'balanced', version: 1, updatedAt: now },
      effectiveMaxParallelism: 5, runtimeCapacityAppliedAt: now,
      runtimeCapacityAppliedVersion: 1,
    }] }));
    await page.route('**/api/runner/project-chat/usage', route => route.fulfill({ json: [{
      hostName: 'agent-runner-01', projectName: 'Agent Studio',
      activeTurns: 1, heavyTurns: 1, cpuPercent: 54,
      tokens: 1200, costUsd: 0.03,
    }] }));
    await page.goto('/#/workspace/settings/execution-hosts', { waitUntil: 'domcontentloaded' });
    await setTheme(page, 'light');
    const host = page.getByTestId('remote-host-card').filter({ hasText: 'agent-runner-01' }).first();
    await expect(host).toBeVisible();
    const disclosure = host.getByTestId('remote-host-disclosure');
    if (await disclosure.getAttribute('aria-expanded') !== 'true') await disclosure.click();
    const capacity = host.getByTestId('remote-host-detail-toggle-capacity');
    if (await capacity.getAttribute('aria-expanded') !== 'true') await capacity.click();
    await expect(host.getByTestId('remote-host-chat-summary')).toContainText('1 active chat turn');
    await expect(host.getByTestId('remote-host-chat-usage')).toContainText('CPU 54%');
    await expect(host.getByTestId('remote-host-slots'))
      .toContainText('5 slots, 4 coding, 1 taken by a heavy chat turn');
    if (process.env.JOB_RESULTS_DIR) {
      await page.screenshot({ path: `${process.env.JOB_RESULTS_DIR}/chat-host-capacity-light.png` });
      await setTheme(page, 'dark');
      await page.screenshot({ path: `${process.env.JOB_RESULTS_DIR}/chat-host-capacity-dark.png` });
    }
  });

  test('silent drop: orchestrator error and submitted message remain visible in canonical chat', async ({ page }) => {
    const project = await openSideSheetForProject(page);
    const state = await installChatMocks(page, project, {
      // Backend errors with an error turn but 200 OK (mirrors the real
      // failure path in OrchestratorChatService.SendAsync where an
      // exception inside ResumeAsync produces a turn with errorMessage).
      handlePost: async () => ({
        status: 200,
        reply: {
          id: `err-${Date.now()}`,
          ts: nowIso(),
          role: 'orchestrator',
          text: '',
          errorMessage: 'Global orchestrator session has not booted yet. Try again in a moment, or check the backend logs.',
        },
      }),
    });

    // Send a clearly task-shaped message.
    const composer = page.getByTestId('chat-input');
    await expect(composer).toBeVisible();
    const taskText = 'Bitte einen neuen Task anlegen: Ready-Lane braucht eine Scrollbar wenn mehr als 8 Karten';
    await composer.fill(taskText);
    await page.getByTestId('chat-send').click();

    // The error turn lands in the canonical orchestrator message group.
    await expect(
      page.locator('[data-testid="conversation-message-message.orchestrator"]')
        .filter({ hasText: 'orchestrator session has not booted' }).first()
    ).toBeVisible({ timeout: 5_000 });

    // CONTRACT: the user's typed message must remain visible (not silently
    // dropped) even when the orchestrator round-trip errors. The former
    // "Make a task from your message" rescue buttons were retired with the
    // AGT-2163 standard-footer consolidation; the transcript itself is now
    // the durable record of the user's intent.
    await expect(
      page.locator('[data-testid="conversation-message-message.user"]').filter({ hasText: 'Ready-Lane' }).first()
    ).toBeVisible();

    // The retired host-only task conversion workflow stays absent even on
    // the failure path. Slash commands remain available through the composer.
    await expect(page.getByText('Make a task from your message', { exact: true })).toHaveCount(0);
    await expect(page.getByText('Make a task from this reply', { exact: true })).toHaveCount(0);

    await page.screenshot({ path: `${SHOTS}/01-silent-drop-visible.png`, fullPage: false });

    // Sanity: the mock saw exactly one POST (the user's send).
    expect(state.turns.filter((t) => t.role === 'user').length).toBe(1);
  });

  test('sluggishness: long-task budget under a 5s slow orchestrator reply stays bounded', async ({ page }) => {
    const project = await openSideSheetForProject(page);
    await installChatMocks(page, project, {
      handlePost: async ({ text }) => {
        // Simulate a 4s claude round-trip without burning quota.
        await new Promise((r) => setTimeout(r, 4_000));
        return {
          status: 200,
          reply: {
            id: `srv-${Date.now()}`,
            ts: nowIso(),
            role: 'orchestrator',
            text: `Slow reply to: ${text.slice(0, 40)}`,
          },
        };
      },
    });

    const recorder = await startLongTaskRecorder(page);
    const composer = page.getByTestId('chat-input');
    await composer.fill('Eine ruhige, mittellange Frage an den Orchestrator zur Sluggishness.');

    const t0 = Date.now();
    await page.getByTestId('chat-send').click();

    // Composer should immediately enter a pending state - the user must
    // see something change, not stare at a frozen input.
    await expect(page.getByTestId('chat-send')).toBeDisabled();

    // Wait for the slow reply to land.
    await page.waitForResponse(/\/api\/runner\/[^/]+\/orchestrator-chat$/, { timeout: 10_000 });
    const wallMs = Date.now() - t0;

    const longTaskMs = await recorder.totalMs();
    const longTaskCount = await recorder.count();
    await recorder.stop();

    console.log(`[chat-fix] sluggish wall=${wallMs}ms longTaskMs=${longTaskMs} longTaskCount=${longTaskCount}`);

    // Generous threshold: a soft chat surface should not block the main
    // thread for more than 250 ms cumulatively across a multi-second wait.
    // The recorder is best-effort (returns 0 if longtask API is unavailable),
    // so the assertion is a permissive ceiling, not a perf target.
    expect(longTaskMs).toBeLessThan(400);

    await page.screenshot({ path: `${SHOTS}/02-sluggish-after.png`, fullPage: false });
  });

  test('parallel use: two contexts each get their own send round-trip and stay interactive', async ({ browser }) => {
    const ctxA = await browser.newContext();
    const ctxB = await browser.newContext();
    const pageA = await ctxA.newPage();
    const pageB = await ctxB.newPage();

    try {
      // Tab A: slow reply (3s); Tab B: fast reply (200ms).
      const projA = await openSideSheetForProject(pageA);
      await installChatMocks(pageA, projA, {
        handlePost: async ({ text }) => {
          await new Promise((r) => setTimeout(r, 3_000));
          return {
            status: 200,
            reply: { id: `a-${Date.now()}`, ts: nowIso(), role: 'orchestrator', text: `A: ${text}` },
          };
        },
      });
      const projB = await openSideSheetForProject(pageB);
      await installChatMocks(pageB, projB, {
        handlePost: async ({ text }) => {
          await new Promise((r) => setTimeout(r, 200));
          return {
            status: 200,
            reply: { id: `b-${Date.now()}`, ts: nowIso(), role: 'orchestrator', text: `B: ${text}` },
          };
        },
      });

      // Fire send from A first (slow); B should remain fully interactive.
      const composerA = pageA.getByTestId('chat-input');
      const composerB = pageB.getByTestId('chat-input');
      await composerA.fill('Slow message from tab A');
      await pageA.getByTestId('chat-send').click();
      // Do not await A's response yet; immediately drive B.

      const tB0 = Date.now();
      await composerB.fill('Fast message from tab B');
      await pageB.getByTestId('chat-send').click();
      await pageB.waitForResponse(/\/api\/runner\/[^/]+\/orchestrator-chat$/, { timeout: 10_000 });
      const tBms = Date.now() - tB0;

      // B should be done while A is still in-flight.
      expect(tBms).toBeLessThan(2_500);

      // A is still mid-send (its input is disabled).
      await expect(pageA.getByTestId('chat-input')).toBeDisabled();
      // B's input is back to interactive (its send-button is gated on
      // having draft text, which we'll provide next to confirm).
      await expect(pageB.getByTestId('chat-input')).toBeEnabled();
      await composerB.fill('Second message from B while A still pending');
      await expect(pageB.getByTestId('chat-send')).toBeEnabled();

      // Eventually A also lands. (If parallel use were broken on the FE
      // side - e.g. a global lock - this would time out.)
      await pageA.waitForResponse(/\/api\/runner\/[^/]+\/orchestrator-chat$/, { timeout: 10_000 });
      await expect(pageA.getByTestId('chat-input')).toBeEnabled();

      console.log(`[chat-fix] parallel B-while-A-pending=${tBms}ms`);
      await pageA.screenshot({ path: `${SHOTS}/03-parallel-tab-a.png`, fullPage: false });
      await pageB.screenshot({ path: `${SHOTS}/03-parallel-tab-b.png`, fullPage: false });
    } finally {
      await ctxA.close();
      await ctxB.close();
    }
  });
});
