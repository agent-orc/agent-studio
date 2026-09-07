import { test, expect } from '@playwright/test';

/**
 * Orchestrator side sheet — Phase 2 visual + behavioural smoke.
 *
 * Verifies the toolbar button toggles a right-hand chat-style side sheet
 * (same flex-collapse pattern as CLI Usage), shows the project switcher
 * when more than one project is watched, and renders orchestrator log
 * entries as chat bubbles. Captures screenshots so the layout can be
 * reviewed in the chat without running the UI.
 */
const SHOTS = 'screenshots/orch-side-sheet';

test.describe('Orchestrator side sheet', () => {
  test('opens via toolbar, shows chat surface, closes again', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    // Move the mouse out of the way so quota tooltips don't pop up while
    // we screenshot. The previous run had a Codex tooltip floating over
    // the side sheet header, which made the layout look broken in the
    // captures even though the UI itself was fine.
    await page.mouse.move(0, 0);

    // Board screenshot before opening — establishes the baseline width.
    await page.waitForTimeout(800);
    await page.screenshot({ path: `${SHOTS}/01-board-closed.png`, fullPage: false });

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();

    const sheet = page.getByTestId('orch-side-sheet');
    await expect(sheet).toBeVisible();

    // Sheet open — give Angular a tick to mount the chat content + run
    // the initial /orchestrator-log fetch.
    await page.waitForTimeout(1200);
    await page.screenshot({ path: `${SHOTS}/02-side-sheet-open.png`, fullPage: false });

    const conversation = page.getByTestId('conversation-view');
    await expect(conversation).toBeVisible();

    const composer = page.getByTestId('chat-input');
    await expect(composer).toBeVisible();
    const sendBtn = page.getByTestId('chat-send');
    await expect(sendBtn).toBeVisible();

    // Tight crop of just the side sheet for layout review.
    const box = await sheet.boundingBox();
    if (box) {
      await page.screenshot({
        path: `${SHOTS}/03-side-sheet-only.png`,
        clip: {
          x: Math.max(0, box.x - 4),
          y: Math.max(0, box.y - 4),
          width: Math.min(page.viewportSize()!.width - box.x + 4, box.width + 8),
          height: box.height + 8
        }
      });
    }

    // Phase 3: real conversation endpoint. Composer is enabled the moment
    // a project is selected — sending kicks the GlobalOrchestrator session
    // and persists both turns. We don't actually send here (that would
    // burn quota in the e2e suite); we just assert the composer is wired
    // and the placeholder describes the new flow.
    await expect(composer).toBeEnabled();
    await expect(composer).toHaveAttribute('placeholder', /Ask about this project/);
    await expect(composer).not.toHaveAttribute('placeholder', /orchestrator/i);

    // Studio uses the standard CAC composer/footer with no host-only task
    // conversion controls or duplicate /task button.
    await expect(page.getByTestId('chat-composer-foot')).toHaveCount(1);
    await expect(page.getByText('Make a task from your message', { exact: true })).toHaveCount(0);
    await expect(page.getByText('Make a task from this reply', { exact: true })).toHaveCount(0);
    await expect(page.getByTestId('chat-toolbar-task')).toHaveCount(0);
    await expect(page.getByTestId('chat-toolbar')).toHaveCount(0);
    await expect(page.getByTestId('chat-composer-context')).toHaveCount(0);

    // Compact-bubble polish demo: type into the composer so the layout
    // captures show the active state with text in the input.
    await composer.fill('Wo stehst du gerade auf diesem Projekt?');
    await page.waitForTimeout(150);
    await expect(page.getByTestId('chat-send')).toBeEnabled();
    await page.mouse.move(0, 0);
    const sheetBoxComposer = await sheet.boundingBox();
    if (sheetBoxComposer) {
      await page.screenshot({
        path: `${SHOTS}/03b-composer-with-text.png`,
        clip: {
          x: Math.max(0, sheetBoxComposer.x - 4),
          y: Math.max(0, sheetBoxComposer.y - 4),
          width: Math.min(page.viewportSize()!.width - sheetBoxComposer.x + 4, sheetBoxComposer.width + 8),
          height: sheetBoxComposer.height + 8
        }
      });
    }

    // Verify the project switcher renders as a searchable typeahead
    // combobox so it scales past a handful of projects (typing filters
    // the list), and picking an option swaps the active thread. The
    // hidden <select> stays in the DOM as an a11y / scripting fallback.
    const combo = page.getByTestId('orch-side-sheet-project-combo');
    await expect(combo).toBeVisible();
    const projectSelect = page.getByTestId('orch-side-sheet-project-select');
    const optionValues = await projectSelect.locator('option').evaluateAll((opts) =>
      (opts as HTMLOptionElement[]).map((o) => o.value)
    );
    if (optionValues.length >= 2) {
      const current = await projectSelect.inputValue();
      const next = optionValues.find((v) => v && v !== current) ?? optionValues[1];
      await combo.click();
      await combo.fill(next.slice(0, 2));
      await page.waitForTimeout(150);
      await page.screenshot({ path: `${SHOTS}/04a-combo-filtering.png`, fullPage: false });
      // Pressing Enter commits the highlighted match. We avoid clicking
      // the floating <li> directly because Playwright's click sequence
      // (mousemove -> mousedown -> mouseup -> click) interleaves with
      // input-blur in ways that make tests flaky on the first run.
      await combo.press('Enter');
      await page.waitForTimeout(400);
      // The combo input is cleared after selection; the placeholder shows
      // the new active project so the user sees what is in front. The
      // hidden <select> mirrors the same signal and is also asserted as
      // a redundant guard against placeholder-binding races.
      await expect(combo).toHaveAttribute('placeholder', next);
      await expect(projectSelect).toHaveValue(next);
      await page.screenshot({ path: `${SHOTS}/04-side-sheet-other-project.png`, fullPage: false });
    }

    // 2026-05-16 sidesheet restructure: the sidesheet is Chat-centric.
    // Roadmap Intake / Send to roadmap was retired; the Task (pure chat),
    // Feed, Logic, Manage CLI, Sessions, and Supervisor tabs are also out
    // of the sheet. The ⚙ button in the sidesheet header opens the
    // orchestrator lifecycle flags; AGT-1812 repointed it from the retired
    // standalone modal to the "Orchestrator" section of the one consolidated
    // Settings view.
    await expect(page.getByTestId('orch-side-sheet-tabs')).toHaveCount(0);
    await expect(page.getByTestId('orch-side-sheet-tab-intake')).toHaveCount(0);
    await expect(page.getByTestId('roadmap-intake-panel')).toHaveCount(0);
    await expect(page.getByTestId('orch-side-sheet-tab-task')).toHaveCount(0);
    await expect(page.getByTestId('orch-side-sheet-tab-feed')).toHaveCount(0);
    await expect(page.getByTestId('orch-side-sheet-tab-logic')).toHaveCount(0);
    await expect(page.getByTestId('orch-side-sheet-tab-cli')).toHaveCount(0);
    await expect(page.getByTestId('orch-side-sheet-tab-sessions')).toHaveCount(0);
    await expect(page.getByTestId('orch-side-sheet-tab-supervisor')).toHaveCount(0);

    // Settings lives in the context menu so the chat header stays compact.
    await page.getByTestId('orch-context-badge').click();
    await expect(page.getByTestId('orch-context-menu')).toBeVisible();
    const settingsBtn = page.getByTestId('orch-side-sheet-settings');
    await expect(settingsBtn).toBeVisible();
    await settingsBtn.click();
    const settingsView = page.getByTestId('workspace-settings-inline');
    await expect(settingsView).toBeVisible();
    await expect(page.getByTestId('orchestrator-config-overlay')).toBeVisible();
    await expect(page.getByTestId('orchestrator-logic-panel')).toBeVisible();
    // The retired standalone modal must be gone.
    await expect(page.getByTestId('orchestrator-settings-modal')).toHaveCount(0);
    await page.mouse.move(0, 0);
    await page.waitForTimeout(300);
    await page.screenshot({ path: `${SHOTS}/06-settings-orchestrator-section.png`, fullPage: false });

    // The Settings view rail groups Orchestrator under Global; switch to a
    // neighbouring Global section and back to prove the rail navigates.
    await page.getByTestId('workspace-settings-rail-appearance').click();
    await expect(page.getByTestId('workspace-appearance-overlay')).toBeVisible();
    await page.getByTestId('workspace-settings-rail-orchestrator').click();
    await expect(page.getByTestId('orchestrator-logic-panel')).toBeVisible();
    await page.waitForTimeout(150);
    await page.screenshot({ path: `${SHOTS}/07-settings-rail-nav.png`, fullPage: false });

    await page.getByRole('tab').filter({ hasText: 'Workspace settings' })
      .getByRole('button', { name: 'Close tab' }).click();
    await expect(settingsView).toHaveCount(0);

    // Final close.
    const closeBtn = page.getByTestId('orch-side-sheet-close');
    if (await closeBtn.isVisible()) {
      await closeBtn.click();
      await page.waitForTimeout(500);
    }
    await page.mouse.move(0, 0);
    await page.screenshot({ path: `${SHOTS}/05-side-sheet-closed.png`, fullPage: false });
  });
});
