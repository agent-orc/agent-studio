import { expect, test, type Page } from '@playwright/test';
import { installFrontendOverride } from '../helpers/frontend-override';

/**
 * End-to-end acceptance: orchestrator-chat content is visible immediately
 * after load and stays visible when a scroll fires while sticky at the
 * bottom — no blank area, no manual scroll (ASS-665, ASS-613 sibling).
 *
 * The orchestrator side sheet renders the transcript through the canonical
 * `<cac-conversation-view>` inside one host-owned scroll container. It used to
 * enable fixed-height virtualisation here; a scroll while sticky at the bottom
 * could then re-derive the window from a 120px estimate and place a phantom
 * bottom spacer under short turns. The host now keeps its mixed-height live
 * rows mounted, but the original visible-tail assertion remains useful: an
 * open-animation reflow, async Markdown growth, or programmatic pin must never
 * hide the newest turn.
 *
 * This spec stubs a deep history of short turns, opens the chat, fires a
 * scroll while sticky at the bottom, and asserts the newest turn stays
 * rendered + on-screen with no `conversation-spacer-bottom`.
 *
 * NOTE ON COVERAGE: the deterministic fail-before/pass-after guard for this
 * fix lives in the unit spec (chat.component.spec.ts → "keeps the newest
 * turn rendered when a scroll fires while sticky-at-bottom"), which freezes
 * the exact sticky-bottom geometry that triggers the window-shrink. This e2e
 * exercises the real side-sheet open path in a browser, but the broken
 * symptom is a sub-frame virtual-window race whose reproduction depends on
 * open-animation reflow timing, so it is best-effort at catching the
 * regression and reliable as an acceptance check on the fixed build.
 */

// Alpha-only: orchestrator turns render through markdown, and underscores
// would be eaten as emphasis (NEWEST_X_Y -> italic, underscores stripped).
const NEWEST_MARKER = 'ZZZNEWESTTURNVISIBLEZZZ';

interface StubTurn {
  id: string;
  ts: string;
  role: 'user' | 'orchestrator';
  text: string;
}

function buildTurns(n: number): StubTurn[] {
  const base = Date.parse('2026-06-04T09:00:00Z');
  const turns: StubTurn[] = [];
  for (let i = 0; i < n; i++) {
    const role = i % 2 === 0 ? 'user' : 'orchestrator';
    // Deliberately short one-liners: their real rendered height is well
    // below the 120px virtual estimate, which is what triggered the bug.
    const text = i === n - 1 ? NEWEST_MARKER : `Short turn ${i}`;
    turns.push({
      id: `turn-${String(i).padStart(3, '0')}`,
      ts: new Date(base + i * 60_000).toISOString(),
      role,
      text,
    });
  }
  return turns;
}

const TURNS = buildTurns(200);

async function stubOrchestratorChat(page: Page): Promise<void> {
  await installFrontendOverride(page);

  await page.route('**/api/watch-paths', route => route.fulfill({
    json: [{ name: 'chat-visible', path: '/tmp/chat-visible', rootPath: '/tmp/chat-visible' }],
  }));
  await page.route('**/api/v1/workspaces', route => route.fulfill({
    json: [{
      id: 'workspace-chat-visible',
      displayName: 'Chat fixture',
      sortOrder: 0,
      isDefault: true,
      projects: [{
        id: 'chat-visible',
        displayName: 'chat-visible',
        shortCode: 'CV',
        workspaceId: 'workspace-chat-visible',
        storageLocation: '/tmp/chat-visible',
        archived: false,
        urls: [],
      }],
    }],
  }));

  // Keep the assertion isolated from unrelated recovery state on a live
  // operator backend. Mutating that recovery state just to reach the chat
  // would be destructive, so make this read empty in the browser fixture.
  await page.route('**/api/crash-recovery/pending', async (route) => {
    if (route.request().method() !== 'GET') {
      await route.fallback();
      return;
    }
    await route.fulfill({ json: { pending: [] } });
  });

  // Exact-path match for the chat GET; the `/attachments/...` sub-route
  // lives deeper so this glob never swallows it.
  await page.route('**/api/runner/*/orchestrator-chat', async (route) => {
    if (route.request().method() !== 'GET') {
      await route.fallback();
      return;
    }
    const project =
      decodeURIComponent(route.request().url().match(/runner\/([^/]+)\/orchestrator-chat/)?.[1] ?? 'demo');
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project, turns: TURNS }),
    });
  });
}

async function openOrchestratorChat(page: Page) {
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 15_000 });
  await toggle.click();

  const sheet = page.getByTestId('orch-side-sheet');
  await expect(sheet).toBeVisible();
  await expect
    .poll(() => sheet.evaluate((el) => (el as HTMLElement).offsetWidth), {
      message: 'orchestrator side sheet width',
    })
    .toBeGreaterThan(300);

  const body = page.getByTestId('conversation-view');
  if (!(await body.count())) {
    test.skip(true, 'No watched project auto-selected — orchestrator chat body did not mount');
  }
  await expect(body).toBeVisible();
  // Wait for the stubbed turns to render. We do NOT wait on the newest
  // marker here: on the broken build it can already be windowed out, so
  // the precondition must be a turn that renders in both builds.
  await expect(page.getByTestId('conversation-message-message.user').first()).toBeVisible();
  return { sheet, body };
}

test.describe('orchestrator chat — content stays visible after load', () => {
  test('newest turn survives a scroll fired while sticky at the bottom', async ({ page }) => {
    await stubOrchestratorChat(page);
    const { body } = await openOrchestratorChat(page);

    // Reproduce the trigger deterministically AND read the result in one
    // shot: park the scroller at the true bottom (sticky), fire a scroll
    // event exactly like the open-animation / markdown-growth / programmatic-
    // pin reflow does, wait two animation frames for the window recompute,
    // then capture marker + spacer state synchronously — all inside a single
    // evaluate. This MUST be a single-shot read: the side sheet polls the
    // chat in the background and re-pins the tail, which heals the transient
    // vanish within a second. A retrying matcher (toBeVisible) would let it
    // heal and pass on the broken build, masking the regression. We snapshot
    // the DOM the instant after the scroll, before any poll can run.
    const snapshot = await body.evaluate(async (el, marker) => {
      const nextFrame = () =>
        new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
      let scroller: HTMLElement | null = el as HTMLElement;
      while (scroller) {
        const overflowY = getComputedStyle(scroller).overflowY;
        if (/auto|scroll|overlay/.test(overflowY)) break;
        scroller = scroller.parentElement;
      }
      if (!scroller) throw new Error('No orchestrator transcript scroller found');
      scroller.scrollTop = scroller.scrollHeight;
      scroller.dispatchEvent(new Event('scroll'));
      await nextFrame();
      await nextFrame();

      const rows = Array.from(
        el.querySelectorAll('[data-testid^="conversation-message-message."]')
      ) as HTMLElement[];
      const markerEl = rows.find((r) => (r.textContent ?? '').includes(marker));
      const containerRect = scroller.getBoundingClientRect();
      const rect = markerEl?.getBoundingClientRect();
      const spacer = el.querySelector(
        '[data-testid="conversation-spacer-bottom"]'
      ) as HTMLElement | null;

      return {
        markerRendered: !!markerEl,
        // Vertically overlapping the scroll container = on screen, no manual
        // scroll needed.
        markerInViewport:
          !!rect &&
          rect.bottom > containerRect.top &&
          rect.top < containerRect.bottom,
        bottomSpacerHeight: spacer ? spacer.getBoundingClientRect().height : 0,
      };
    }, NEWEST_MARKER);

    // The newest turn must be rendered AND on screen — no manual scroll.
    expect(snapshot.markerRendered).toBe(true);
    expect(snapshot.markerInViewport).toBe(true);
    // And there must be no phantom bottom spacer hiding the tail.
    expect(snapshot.bottomSpacerHeight).toBe(0);

    const screenshotPath = test.info().outputPath('newest-turn-visible-after-load.png');
    await page.screenshot({ path: screenshotPath });
    await test.info().attach('newest-turn-visible-after-load.png', {
      path: screenshotPath,
      contentType: 'image/png',
    });
  });
});
