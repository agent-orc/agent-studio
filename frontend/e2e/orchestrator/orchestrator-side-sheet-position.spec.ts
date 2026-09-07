import { test, expect } from '../fixtures/dev-backend';

/**
 * Regression coverage for the right-side-sheet contract.
 *
 * The chat surface must:
 *  - open as a persistent right-side panel (not a centered modal)
 *  - keep the board / workspace visible behind it (no backdrop overlay)
 *  - sit on the right edge of the viewport
 *  - not be titled "Project window" (which previously implied it owned
 *    the project shell window)
 *
 * The 2026-05-11 crash-recovery commit accidentally promoted the chat
 * into a centered "Project window" modal that covered the board; this
 * spec locks the position so it cannot regress without a red test.
 *
 * Uses the dev-backend fixture so dev's :5030 is brought up before the
 * spec runs (and stopped after if the fixture started it). Without the
 * fixture, the dev frontend renders the "backend unreachable" error
 * dialog over the page and the toggle button cannot be clicked.
 */
const SHOTS = 'screenshots/orch-side-sheet-position';

test.describe('Orchestrator side sheet position', () => {
  test.beforeEach(async ({ page }) => {
    await page.route(/\/api\/tasks\/grouped(?:\?.*)?$/, route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], autoReview: [], humanReview: [], review: [], completed: [], archive: [],
      }),
    }));
  });

  test('opens as a right-side panel that leaves the board visible', async ({ page, devBackend: _ }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.mouse.move(0, 0);
    await page.waitForTimeout(500);

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });

    // Studio shell hosts the board (Cycle 9 redesign). The legacy
    // `kanban-dashboard` testid still hangs on the board <main> when the
    // active tab is the board, but the load-bearing assertion is the
    // studio-shell box: a wide non-zero box behind the open chat panel
    // proves the chat does not overlay the workspace.
    const board = page.locator('app-studio-shell');
    await expect(board).toBeVisible();
    const boardBoxBefore = await board.boundingBox();
    expect(boardBoxBefore).not.toBeNull();

    await toggle.click();
    const sheet = page.getByTestId('orch-side-sheet');
    await expect(sheet).toBeVisible();
    await page.waitForTimeout(400);

    await page.screenshot({ path: `${SHOTS}/01-open.png`, fullPage: false });

    // Title is no longer the misleading "Project window" — sidesheet
    // wrapper paints "Orchestrator" as the title.
    await expect(sheet.locator('.sidesheet__title')).toHaveText(/^(Chat|Orchestrator)/i);
    await expect(sheet).not.toContainText('Project window');

    // Sheet sits on the right edge of the viewport: its right edge is at
    // (or within a few px of) the viewport's right edge, and its left
    // edge is well past the viewport's left half. This is the load-
    // bearing assertion that distinguishes "right side sheet" from
    // "centered modal" (the previous regression had the panel margin-auto
    // centered with ~50px gutters on both sides).
    const viewport = page.viewportSize();
    expect(viewport).not.toBeNull();
    const sheetBox = await sheet.boundingBox();
    expect(sheetBox).not.toBeNull();
    expect(sheetBox!.x + sheetBox!.width).toBeGreaterThan(viewport!.width - 8);
    expect(sheetBox!.x).toBeGreaterThan(viewport!.width * 0.4);

    // Sheet is narrower than the viewport — no centered-modal mode with
    // 90+ % width. 80 % is the cutoff: the new layout caps at 640px,
    // which is well under 80 % of a 1280-wide viewport but allows for
    // narrower viewports to still pass.
    expect(sheetBox!.width).toBeLessThan(viewport!.width * 0.8);

    // Board is still mounted and at least partially visible — the sheet
    // does not paint a full-viewport backdrop over the workspace.
    const boardBoxAfter = await board.boundingBox();
    expect(boardBoxAfter).not.toBeNull();
    expect(boardBoxAfter!.width).toBeGreaterThan(120);

    // No backdrop element from the centered-modal era. The previous
    // implementation laid down a `position: fixed; inset: 0` overlay
    // before the sheet; we look for any element that fully covers the
    // viewport behind the sheet (other than the sheet host itself).
    const fullCoverElements = await page.evaluate(() => {
      const out: { tag: string; cls: string }[] = [];
      const vw = window.innerWidth;
      const vh = window.innerHeight;
      for (const el of Array.from(document.body.querySelectorAll('*'))) {
        const r = (el as HTMLElement).getBoundingClientRect();
        const cs = window.getComputedStyle(el);
        if (cs.position !== 'fixed') continue;
        if (r.width < vw - 4 || r.height < vh - 4) continue;
        if ((el as HTMLElement).closest('[data-testid="orch-side-sheet"]')) continue;
        out.push({ tag: el.tagName.toLowerCase(), cls: (el as HTMLElement).className || '' });
      }
      return out;
    });
    expect(fullCoverElements).toEqual([]);

    // Composer is reachable inside the side sheet — proves the chat
    // surface (not a placeholder) is what the side sheet hosts.
    await expect(page.getByTestId('chat-input')).toBeVisible();

    // Close and re-open to confirm the flex-collapse pattern (host width
    // returns to zero so the board reclaims its full width). The close
    // button lives on the shared `<app-sidesheet>` chrome.
    await sheet.getByTestId('sidesheet-close').click();
    await page.waitForTimeout(400);
    const boardBoxClosed = await board.boundingBox();
    expect(boardBoxClosed).not.toBeNull();
    expect(boardBoxClosed!.width).toBeGreaterThanOrEqual(boardBoxAfter!.width);

    await page.screenshot({ path: `${SHOTS}/02-closed.png`, fullPage: false });
  });

  /**
   * Push contract (not overlay). Opening the orchestrator chat must
   * narrow the editor area by approximately the panel width so the
   * workspace stays visible alongside the chat; the inner
   * `<app-sidesheet>` chrome must fill the full open host width (no
   * transparent gap on the right); the panel host must stay in normal
   * flow (position: static / relative, never `fixed` or `absolute`).
   *
   * Architecture note: the orchestrator rail is now projected INSIDE
   * the studio-shell body grid (trailing `auto` track), so opening it
   * leaves the studio-shell box at full viewport width and narrows the
   * editor track instead. This keeps the titlebar + statusbar spanning
   * the full width — VS-Code-style. The earlier "studio-shell narrows"
   * test was written before that change.
   *
   * Three earlier regressions are still pinned here:
   *  - The host was `position: fixed` via a studio-mode workaround in
   *    styles.scss, which forced overlay behaviour and prevented any
   *    push. Fixed by moving the host into the grid track.
   *  - The inner `.sidesheet` div had `width: 360px` hard-coded in
   *    `components/sidesheet/sidesheet.component.scss`, leaving a
   *    transparent 280 px gap inside the open 640 px host.
   *  - The editor and orchestrator must not overlap along x — the
   *    orchestrator's left edge must be at or beyond the editor's
   *    right edge.
   */
  test('open narrows editor + inner panel fills host', async ({ page, devBackend: _ }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(400);

    const editor = page.locator('app-studio-shell .studio-editor');
    const editorWidthClosed = (await editor.boundingBox())!.width;

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();
    await page.getByTestId('orch-side-sheet').waitFor({ state: 'visible' });
    await page.waitForTimeout(450);

    const editorWidthOpen = (await editor.boundingBox())!.width;

    const geometry = await page.evaluate(() => {
      const orchHost = document.querySelector('app-orchestrator-side-sheet') as HTMLElement | null;
      const inner = document.querySelector('[data-testid="orch-side-sheet"]') as HTMLElement | null;
      const studio = document.querySelector('app-studio-shell') as HTMLElement | null;
      const editor = document.querySelector('app-studio-shell .studio-editor') as HTMLElement | null;
      function box(el: HTMLElement | null) {
        if (!el) return null;
        const r = el.getBoundingClientRect();
        const cs = window.getComputedStyle(el);
        return {
          width: Math.round(r.width),
          x: Math.round(r.x),
          right: Math.round(r.x + r.width),
          position: cs.position,
        };
      }
      return {
        orchHost: box(orchHost),
        inner: box(inner),
        studio: box(studio),
        editor: box(editor),
        vw: window.innerWidth
      };
    });

    // Inner sidesheet fills its open host (no gap on the right).
    expect(geometry.inner!.width).toBe(geometry.orchHost!.width);

    // Push, not overlay: the editor track narrows by approximately the
    // panel width on open. Without this assertion the regression where
    // the orchestrator visually overlays the workspace would slip
    // through silently.
    expect(editorWidthClosed - editorWidthOpen).toBeGreaterThan(400);

    // The editor's right edge must end where the orchestrator's left
    // edge begins — no overlap along x.
    expect(geometry.editor!.right).toBeLessThanOrEqual(geometry.orchHost!.x + 1);

    // Orchestrator host sits on the right edge of the viewport.
    expect(geometry.orchHost!.right).toBeGreaterThanOrEqual(geometry.vw - 4);

    // Host stays in normal flow (not `position: fixed`, which was the
    // overlay regression). `static` or `relative` are both fine —
    // `relative` is needed since the panel resize splitter anchors
    // absolutely to the host's box. `fixed` / `absolute` would re-
    // introduce the overlay bug.
    expect(['static', 'relative']).toContain(geometry.orchHost!.position);
  });

  /**
   * Resize splitter contract. The user can drag the panel's left edge
   * to widen / narrow the orchestrator, and the chosen width survives a
   * reload via localStorage (key `atp.studio.orchestratorWidth`).
   */
  test('resize splitter widens the panel and persists across reloads', async ({ page, devBackend: _ }) => {
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    // Clear any persisted width from earlier test runs so we start
    // from a known baseline. Done after goto so the cleanup is one-shot
    // and does NOT re-run on the page.reload() further down (which
    // would defeat the whole "did the width persist?" assertion).
    await page.evaluate(() => {
      try { localStorage.removeItem('atp.studio.orchestratorWidth'); } catch { /* ignore */ }
    });
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(300);

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();
    const host = page.locator('app-orchestrator-side-sheet');
    await host.waitFor({ state: 'visible' });
    await page.waitForTimeout(400);

    const widthBefore = (await host.boundingBox())!.width;
    const handle = page.getByTestId('orch-side-sheet-resize');
    await expect(handle).toBeVisible();

    // Drag the handle 200 px to the LEFT (handle is on the panel's left
    // edge; the orchestrator is on the viewport's right edge → dragging
    // left widens the panel).
    const handleBox = (await handle.boundingBox())!;
    const startX = handleBox.x + handleBox.width / 2;
    const startY = handleBox.y + handleBox.height / 2;
    await page.mouse.move(startX, startY);
    await page.mouse.down();
    await page.mouse.move(startX - 200, startY, { steps: 10 });
    await page.mouse.up();
    await page.waitForTimeout(150);

    const widthAfter = (await host.boundingBox())!.width;
    // Drag delta should land within ~20 px of the requested 200 (clamp +
    // sub-pixel rounding). Lower bound is the load-bearing assertion;
    // upper bound just guards against runaway resize.
    expect(widthAfter - widthBefore).toBeGreaterThan(150);
    expect(widthAfter - widthBefore).toBeLessThan(250);

    const persisted = await page.evaluate(() => localStorage.getItem('atp.studio.orchestratorWidth'));
    expect(persisted).not.toBeNull();
    expect(parseInt(persisted!, 10)).toBeGreaterThan(widthBefore + 150);

    // Reload — width must come back from localStorage and the panel
    // re-opens to the same width once toggled.
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(300);
    const reloadedToggle = page.getByTestId('orch-side-sheet-toggle');
    if (await reloadedToggle.getAttribute('aria-pressed') !== 'true') await reloadedToggle.click();
    await expect(reloadedToggle).toHaveAttribute('aria-pressed', 'true');
    await host.waitFor({ state: 'visible' });
    await page.waitForTimeout(400);
    const widthAfterReload = (await host.boundingBox())!.width;
    expect(Math.abs(widthAfterReload - widthAfter)).toBeLessThan(4);
  });

  /**
   * Compact-viewport push contract. At 1280 × 720 the orchestrator sheet
   * (640 px) leaves ~640 px for the studio-shell, which is less than the
   * sum of the activity bar (48) + the default user-chosen sidebar
   * (240 px) + editor min-content. The earlier layout pinned the sidebar
   * to its user width via inline `[style.width.px]` and used a non-
   * shrinkable grid track, so the studio-shell intrinsic width grew past
   * its container box and `.app-shell { overflow: hidden }` clipped the
   * right edge of the editor — visually it looked like the orchestrator
   * was painting over the editor.
   *
   * The fix: grid uses `minmax(0, sidebarWidth)px` for the sidebar track
   * and `min-width: 0` on `.studio-sidebar`, so the sidebar shrinks under
   * pressure while the user-chosen width remains the *cap*. This test
   * pins it: the editor's right edge stays inside the studio-shell box
   * when the sheet opens on a 1280 px viewport.
   */
  test('push contract holds on a 1280 px viewport (no editor clip)', async ({ page, devBackend: _ }) => {
    await page.setViewportSize({ width: 1280, height: 720 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(400);

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();
    await page.getByTestId('orch-side-sheet').waitFor({ state: 'visible' });
    await page.waitForTimeout(450);

    const geometry = await page.evaluate(() => {
      function box(el: Element | null) {
        if (!el) return null;
        const r = (el as HTMLElement).getBoundingClientRect();
        return { x: Math.round(r.x), width: Math.round(r.width), right: Math.round(r.x + r.width) };
      }
      return {
        studio: box(document.querySelector('app-studio-shell')),
        editor: box(document.querySelector('app-studio-shell .studio-editor')),
        orchHost: box(document.querySelector('app-orchestrator-side-sheet')),
        vw: window.innerWidth,
      };
    });

    // The orchestrator sits on the right edge of the viewport.
    expect(geometry.orchHost!.right).toBeGreaterThanOrEqual(geometry.vw - 4);
    // The editor ends where the orchestrator begins — no overlap. This
    // is what fails when the sidebar refuses to shrink and pushes the
    // editor right behind the chat panel.
    expect(geometry.editor!.right).toBeLessThanOrEqual(geometry.orchHost!.x + 1);
    // The orchestrator must stay inside the studio-shell box (it's a
    // projected child of `.studio-body`, not a sibling), and studio
    // itself must span the full viewport so the titlebar and statusbar
    // remain end-to-end.
    expect(geometry.orchHost!.right).toBeLessThanOrEqual(geometry.studio!.right + 1);
  });
});
