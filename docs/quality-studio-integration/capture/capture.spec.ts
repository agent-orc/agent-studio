// Pinned captures for docs/quality-studio-integration/index.html (AGT-3352).
// Real Studio UI over the isolated ADR-0056 demo workspace. Nothing is
// mocked: the fixture seeds scripts/seed-demo-workspace.mjs and starts the
// backend through frontend/e2e/fixtures/dev-backend.ts.
import { test, expect } from '../../../frontend/e2e/fixtures/dev-backend';
import { setTheme } from '../../../frontend/e2e/helpers/theme';
import * as fs from 'node:fs';
import * as path from 'node:path';

type Page = Parameters<Parameters<typeof test>[2]>[0]['page'];

test.use({ demoWorkspace: true });

const OUT = path.resolve(__dirname, '..', 'assets');
const CARD = '[data-testid="task-card"], [data-testid="job-card"]';
const FIXED_BROWSER_TIME = '2026-08-09T13:00:00.000Z';
const THEMES = ['light', 'dark'] as const;
const manifest: { capturedAt: string; provenance: string; frames: { file: string; claim: string }[] } = {
  capturedAt: new Date().toISOString(),
  provenance: 'Agent Studio frontend production build served by frontend/scripts/serve-prebuilt-frontend.mjs, dev backend from this worktree started by frontend/e2e/fixtures/dev-backend.ts with demoWorkspace (ADR-0056 pinned seed). Browser clock fixed at 2026-08-09T13:00:00Z.',
  frames: [],
};

async function settle(page: Page): Promise<void> {
  await page.evaluate(async () => {
    await document.fonts.ready;
    await new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
  });
}

async function hideDevChrome(page: Page): Promise<void> {
  await page.addStyleTag({
    content: `body::before, .dev-banner, [data-testid="dev-banner"] { display: none !important; }
      [data-testid="status-bar"] .statusbar__quota { visibility: hidden !important; }`,
  });
}

async function dismissOverlays(page: Page): Promise<void> {
  for (let i = 0; i < 12; i++) {
    const dismiss = page.getByTestId('crash-recovery-dismiss').first();
    if (await dismiss.isVisible().catch(() => false)) {
      await dismiss.click({ force: true }).catch(() => undefined);
      await page.waitForTimeout(300);
      continue;
    }
    if (i < 3) { await page.waitForTimeout(400); continue; }
    break;
  }
}

async function openBoard(page: Page): Promise<void> {
  await page.clock.setFixedTime(FIXED_BROWSER_TIME);
  await page.goto('/#/board');
  await hideDevChrome(page);
  await dismissOverlays(page);
  const trigger = page.getByTestId('studio-project-picker-trigger');
  await expect(trigger).toBeVisible({ timeout: 60_000 });
  const demoCard = page.locator(CARD).filter({ hasText: 'DEMO-9' }).first();
  // The picker remembers Demo App after the first visit; select it only once.
  if (!await demoCard.isVisible().catch(() => false)) {
    await trigger.click();
    await page.getByTestId('studio-project-picker-panel').getByText('Demo App', { exact: true }).click();
  }
  await expect(demoCard).toBeVisible({ timeout: 60_000 });
}

async function shotBoth(page: Page, name: string, claim: string, target?: () => ReturnType<Page['locator']>): Promise<void> {
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await settle(page);
    const file = `${name}-${theme}--pinned.png`;
    const options = { path: path.join(OUT, file), animations: 'disabled' as const, caret: 'hide' as const };
    if (target) await target().screenshot(options);
    else await page.screenshot({ ...options, fullPage: false });
    manifest.frames.push({ file, claim });
  }
}

test('quality-studio-integration pinned captures', async ({ page, devBackend }) => {
  expect(devBackend.port).toBeGreaterThan(0);
  fs.mkdirSync(OUT, { recursive: true });
  const pageErrors: string[] = [];
  page.on('pageerror', (error) => pageErrors.push(error.message));

  // 1. Project Deck rail: the Quality group as it exists today.
  await openBoard(page);
  const projectRow = page.getByTestId('studio-explorer-project-Demo App');
  await expect(projectRow).toBeVisible();
  const deck = page.getByRole('button', { name: 'Deck, Demo App', exact: true });
  if (!await deck.isVisible().catch(() => false)) {
    await projectRow.getByRole('button', { name: /^Demo App, / }).first().click();
  }
  await deck.click();
  await expect(page.getByTestId('project-shell-group-quality')).toBeVisible({ timeout: 60_000 });
  for (const key of ['security', 'test-quality', 'audits', 'proposals']) {
    await expect(page.getByTestId(`project-shell-rail-${key}`)).toBeVisible();
  }
  await page.getByTestId('project-shell-rail-security').click();
  await shotBoth(page, 'project-deck-quality-group',
    'Project Deck rail lists Security, Test Quality, Audits & Checks and Proposals under Quality; no Quality Studio entry.');

  // 2. Card Evidence tab: review evidence rows (DEMO-9 seed).
  await openBoard(page);
  const decision = page.locator(CARD).filter({ hasText: 'DEMO-9' }).first();
  await decision.scrollIntoViewIfNeeded();
  await decision.click();
  await page.getByTestId('prompt-tab-evidence').click();
  const panel = page.getByTestId('review-evidence-panel');
  await expect(panel).toBeVisible();
  await expect(page.getByTestId('review-evidence-row-review-empty-table-assertion')).toBeVisible();
  await expect(page.getByTestId('review-evidence-row-browser-proof-both-themes')).toBeVisible();
  // The escalation block above the panes pushes the panel below the fold at
  // 1000 px; a taller viewport keeps the whole panel inside the pane.
  await page.setViewportSize({ width: 1600, height: 1800 });
  await panel.scrollIntoViewIfNeeded();
  await shotBoth(page, 'card-evidence-review-panel',
    'Card Evidence tab: Review evidence lists per-row source, severity, run index and file reference; no commit range or external verdict.',
    () => page.getByTestId('evidence-view'));
  await page.setViewportSize({ width: 1600, height: 1000 });

  // 3. Card Code Review tab: the Agent Studio grade (DEMO-5 seed).
  await openBoard(page);
  const task = page.locator(CARD).filter({ hasText: 'DEMO-5' }).first();
  await task.scrollIntoViewIfNeeded();
  await task.click();
  await expect(page.getByTestId('pane-protocol')).toBeVisible();
  await page.getByTestId('prompt-tab-code-review').click();
  await expect(page.getByTestId('code-review-panel')).toBeVisible();
  await expect(page.getByTestId('code-review-list')).toBeVisible();
  await shotBoth(page, 'card-code-review-grade',
    'Card Code Review tab shows the Agent Studio review grade and verdict per review run.');

  expect(pageErrors, 'page errors during capture').toEqual([]);
  fs.writeFileSync(path.join(OUT, 'capture-manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
});
