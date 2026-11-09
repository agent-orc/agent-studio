import { test, expect, type Page } from '@playwright/test';

// On-demand visual documentation screenshot generator. It uses the versioned
// pinned workspace seeded by scripts/seed-demo-workspace.mjs and writes output
// paths relative to the frontend/ working dir.

const OUT = '../docs/assets/images/';
const TASK_CARD = '[data-testid="task-card"], [data-testid="job-card"]';
const PRIMARY_TASK_LABEL = 'DEMO-9';
const DEMO_APP = 'Demo App';
const FIXED_BROWSER_TIME = '2026-08-09T13:00:00.000Z';

async function applyVisualCaptureMode(page: Page): Promise<void> {
  if ((process.env.PW_VISUAL_CAPTURE ?? 'marketing') !== 'marketing') return;

  await page.addStyleTag({
    content: `
      body::before,
      .dev-banner,
      [data-testid="dev-banner"] {
        display: none !important;
      }
    `,
  });
}

async function capture(page: Page, fileName: string) {
  await page.evaluate(() => new Promise<void>((resolve) => {
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
  }));
  await page.screenshot({
    path: `${OUT}${fileName}`,
    fullPage: false,
    animations: 'disabled',
    caret: 'hide',
  });
}

async function openPinnedTask(page: Page, label: string): Promise<void> {
  const card = page.locator(TASK_CARD).filter({ hasText: label }).first();
  await expect(card).toBeVisible({ timeout: 15_000 });
  await card.scrollIntoViewIfNeeded();
  await card.click();
}

async function clickVisibleTestId(page: Page, testIds: readonly string[]): Promise<void> {
  for (const testId of testIds) {
    const locator = page.getByTestId(testId).first();
    if (await locator.isVisible()) {
      await locator.click();
      return;
    }
  }

  await page.getByTestId(testIds[0]).first().click({ force: true });
}

async function dismissBlockingOverlays(page: Page): Promise<void> {
  // The dev backend can surface a "crash recovery" overlay at startup when it
  // finds uncommitted working-tree changes. It intercepts pointer events and
  // ruins the board shot. Dismiss it non-destructively — "Leave uncommitted"
  // (data-testid="crash-recovery-dismiss") leaves the changes alone.
  // It can render a moment after load, and there is one card per project, so poll
  // on the first few passes and keep dismissing until none remain.
  for (let i = 0; i < 40; i++) {
    const dismiss = page.getByTestId('crash-recovery-dismiss').first();
    if (await dismiss.isVisible().catch(() => false)) {
      await dismiss.click({ force: true }).catch(() => undefined);
      await page.waitForTimeout(350);
      continue;
    }
    if (i < 4) { await page.waitForTimeout(500); continue; }
    break;
  }
  // Belt and braces: close any leftover modal overlay so clicks land on the board.
  const overlay = page.getByTestId('studio-overlay-root').first();
  if (await overlay.isVisible().catch(() => false)) {
    await page.keyboard.press('Escape').catch(() => undefined);
    await page.waitForTimeout(300);
  }
}

test.describe.configure({ mode: 'serial' });

test.use({ viewport: { width: 1440, height: 900 } });

async function freezeBrowserClock(page: Page): Promise<void> {
  await page.clock.setFixedTime(FIXED_BROWSER_TIME);
}

async function selectDemoApp(page: Page): Promise<void> {
  const trigger = page.getByTestId('studio-project-picker-trigger');
  await expect(trigger).toBeVisible({ timeout: 15_000 });
  await trigger.click();
  await page.getByTestId('studio-project-picker-panel').getByText(DEMO_APP, { exact: true }).click();
}

test('readme screenshots - board and task detail states', async ({ page }) => {
  await freezeBrowserClock(page);

  await page.goto('/');
  await applyVisualCaptureMode(page);
  await dismissBlockingOverlays(page);
  await expect(page.getByTestId('dev-banner')).toBeHidden({ timeout: 5_000 });
  await selectDemoApp(page);
  await expect(page.locator(TASK_CARD).first()).toBeVisible({ timeout: 15_000 });
  await capture(page, 'board-overview--pinned.png');

  await openPinnedTask(page, PRIMARY_TASK_LABEL);
  await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('overview-tab')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-overview--pinned.png');

  await page.getByTestId('prompt-tab-description').click();
  await expect(page.getByTestId('files-pane')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-files--pinned.png');

  await page.getByTestId('prompt-tab-timeline').click();
  await expect(page.getByTestId('timeline-tab')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-timeline--pinned.png');

  await page.getByTestId('prompt-tab-evidence').click();
  await expect(page.getByTestId('evidence-view')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-evidence--pinned.png');

  await page.getByTestId('prompt-tab-code-review').click();
  await expect(page.getByTestId('code-review-panel')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-code-review--pinned.png');

  await page.getByTestId('prompt-tab-overview').click();
  await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-protocol--pinned.png');

  await page.getByTestId('inspector-tab-activity').click();
  await expect(page.getByTestId('activity-panel')).toBeVisible({ timeout: 10_000 });
  await capture(page, 'detail-activity--pinned.png');

  await clickVisibleTestId(page, ['studio-pane-toggle-git', 'pane-toggle-git']);
  await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 5_000 });
  await capture(page, 'detail-three-panes--pinned.png');

  await clickVisibleTestId(page, ['studio-pane-toggle-prompt', 'pane-toggle-prompt']);
  await clickVisibleTestId(page, ['studio-pane-toggle-protocol', 'pane-toggle-protocol']);
  await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 5_000 });
  await capture(page, 'detail-git-focus--pinned.png');

  await clickVisibleTestId(page, ['studio-pane-toggle-prompt', 'pane-toggle-prompt']);
  await clickVisibleTestId(page, ['studio-pane-toggle-protocol', 'pane-toggle-protocol']);
  await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 5_000 });
  const protocolTab = page.getByTestId('inspector-tab-protocol');
  if (await protocolTab.isVisible() && await protocolTab.isEnabled()) {
    await protocolTab.click();
  }
  await capture(page, 'detail-quality-gate--pinned.png');
});

function slugForProject(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

async function softVisible(page: Page, testId: string, timeout = 15_000): Promise<boolean> {
  return page.getByTestId(testId).first().waitFor({ state: 'visible', timeout }).then(() => true).catch(() => false);
}

// Dismiss only the crash-recovery cards — NO Escape. The project detail view is
// itself rendered inside a `studio-overlay-root`, so pressing Escape (as the
// board-level dismissBlockingOverlays does) would close the project back to the board.
async function dismissCrashRecoveryCards(page: Page): Promise<void> {
  for (let i = 0; i < 20; i++) {
    const card = page.getByTestId('crash-recovery-dismiss').first();
    if (await card.isVisible().catch(() => false)) {
      await card.click({ force: true }).catch(() => undefined);
      await page.waitForTimeout(300);
      continue;
    }
    if (i < 3) { await page.waitForTimeout(400); continue; }
    break;
  }
}

test('readme screenshots - project rails (wiki / tokens / pipeline / orchestrator)', async ({ page }) => {
  test.setTimeout(180_000);
  await freezeBrowserClock(page);
  const projectName = DEMO_APP;
  const slug = slugForProject(projectName);

  // Clear the startup crash-recovery overlay ONCE up front. Dismissing acknowledges
  // each item server-side (tasks.dismissCrashRecovery), so it does not re-surface on
  // later navigations — and while it is up it blocks the project route from
  // resolving (the app falls back to the board).
  await page.goto('/');
  await applyVisualCaptureMode(page);
  await page.waitForTimeout(1500);
  await dismissBlockingOverlays(page);

  // Each rail reproduces exactly the navigation the project-wiki-section spec uses
  // (which is proven to resolve): land on the board, clear persisted shell/panel
  // state — otherwise the app restores the last-open board tab and ignores the
  // deep-link — then deep-link `#/projects/<slug>/<rail>` as a fresh fragment change.
  async function showRail(rail: string, confirmTestId: string): Promise<void> {
    await page.goto('/');
    await page.evaluate(() => {
      for (const key of Object.keys(localStorage)) {
        if (key.startsWith('atp.projectWiki.v1.') || key.startsWith('atp.projectShell.v1.')) localStorage.removeItem(key);
      }
      localStorage.removeItem('atp.studio.panelState.v1');
    });
    await page.goto(`/#/projects/${slug}/${rail}`);
    await applyVisualCaptureMode(page);
    await dismissCrashRecoveryCards(page);
    await softVisible(page, confirmTestId, 20_000);
    await page.waitForTimeout(900);
  }

  // (A) Context management — the project Wiki / knowledge tree, with a doc open.
  await showRail('wiki', 'project-shell-panel-wiki');
  const tree = page.getByTestId('project-wiki-tree');
  if (await tree.isVisible().catch(() => false)) {
    const firstFile = tree.locator('[data-testid^="project-wiki-file-"]').first();
    if (await firstFile.count()) {
      await firstFile.click().catch(() => undefined);
      await softVisible(page, 'project-wiki-viewer', 8_000);
      await page.waitForTimeout(400);
    }
  }
  await capture(page, 'wiki-context--pinned.png');

  // (B) Token economy — per-project usage cards + heatmap + cost (pricing).
  await showRail('token-usage', 'project-token-usage-panel');
  await capture(page, 'token-economy--pinned.png');

  // (C) Pre/core/post step management — the project pipeline catalogue.
  await showRail('pipeline', 'project-detail-pipeline');
  await capture(page, 'pipeline-page--pinned.png');

  // (D) Agent orchestration — the orchestrator rail (full panel).
  await showRail('orchestrator', 'project-shell-panel-orchestrator');
  await capture(page, 'orchestrator-rail--pinned.png');
});
