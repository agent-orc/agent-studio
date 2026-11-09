import { test, expect, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme, type Theme } from '../helpers/theme';

/**
 * Style-rule delivery Dossier evidence over the isolated worktree test stack
 * (`scripts/worktree-test-stack.sh up --with-frontend`, no demo profile: the
 * public-demo profile refuses plain HTTP since AGT-2669). `run-capture.sh`
 * registers a temporary git repository as project "Rule Capture" and creates
 * one card (RC-1) through the public API; this spec only reads what the real
 * backend returns for that card. No route mocks, no injected product state.
 * The only UI interactions are tab clicks and expanding the collapsed pre-step
 * pipeline section so its step labels are legible.
 */
test.describe.configure({ mode: 'serial' });
test.use({
  viewport: { width: 2560, height: 1000 },
  deviceScaleFactor: 1,
  reducedMotion: 'reduce',
});

const ROOT = path.resolve(process.cwd(), '../docs/style-rule-delivery');
const OUT = path.join(ROOT, 'assets');
const FACTS = path.join(ROOT, 'capture', 'rule-delivery-facts.json');
const TASK_CARD = '[data-testid="task-card"], [data-testid="job-card"]';
const PROJECT = 'Rule Capture';
const CARD_KEY = 'RC-1';
const FIXED_BROWSER_TIME = '2026-08-09T13:00:00.000Z';
const THEMES = ['light', 'dark'] as const satisfies readonly Theme[];
const MAX_PNG_BYTES = 2 * 1024 * 1024;

const facts: Record<string, unknown> = {};
let pageErrors: string[] = [];

test.beforeAll(() => {
  fs.mkdirSync(OUT, { recursive: true });
});

test.beforeEach(async ({ page }) => {
  pageErrors = [];
  page.on('pageerror', (error) => pageErrors.push(error.stack ?? `${error.name}: ${error.message}`));
});

test.afterEach(() => {
  expect(pageErrors, `page errors:\n${pageErrors.join('\n---\n')}`).toEqual([]);
});

test.afterAll(() => {
  fs.mkdirSync(path.dirname(FACTS), { recursive: true });
  fs.writeFileSync(FACTS, `${JSON.stringify(facts, null, 2)}\n`);
});

async function applyMarketingMode(page: Page): Promise<void> {
  expect(process.env.PW_VISUAL_CAPTURE ?? 'marketing').toBe('marketing');
  await page.addStyleTag({
    content: `
      body::before,
      .dev-banner,
      [data-testid="dev-banner"] {
        display: none !important;
      }
    `,
  });
  await expect(page.getByTestId('dev-banner')).toBeHidden();
}

async function settle(page: Page): Promise<void> {
  await page.evaluate(() => new Promise<void>((resolve) => {
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
  }));
  await expect(page.locator('body')).toBeVisible();
}

async function dismissBlockingOverlays(page: Page): Promise<void> {
  for (let i = 0; i < 40; i++) {
    const dismiss = page.getByTestId('crash-recovery-dismiss').first();
    if (await dismiss.isVisible().catch(() => false)) {
      await dismiss.click({ force: true }).catch(() => undefined);
      await page.waitForTimeout(350);
      continue;
    }
    if (i < 4) {
      await page.waitForTimeout(500);
      continue;
    }
    break;
  }
}

async function openBoard(page: Page): Promise<void> {
  await page.clock.setFixedTime(FIXED_BROWSER_TIME);
  await page.goto('/');
  await applyMarketingMode(page);
  await dismissBlockingOverlays(page);
  const trigger = page.getByTestId('studio-project-picker-trigger');
  await expect(trigger).toBeVisible({ timeout: 15_000 });
  await trigger.click();
  await page.getByTestId('studio-project-picker-panel').getByText(PROJECT, { exact: true }).click();
  await expect(page.locator(TASK_CARD).filter({ hasText: CARD_KEY }).first()).toBeVisible({ timeout: 30_000 });
}

async function openDemoTask(page: Page): Promise<void> {
  await openBoard(page);
  const card = page.locator(TASK_CARD).filter({ hasText: CARD_KEY }).first();
  await card.scrollIntoViewIfNeeded();
  await card.click();
  await expect(page.getByTestId('pane-protocol')).toBeVisible();
}

async function elementShot(
  page: Page,
  testInfo: TestInfo,
  locator: ReturnType<Page['locator']>,
  name: string,
): Promise<{ file: string; width: number; height: number; bytes: number }> {
  const filePath = path.join(OUT, `${name}.png`);
  await settle(page);
  const png = await locator.screenshot({ path: filePath, animations: 'disabled', caret: 'hide' });
  const width = png.readUInt32BE(16);
  const height = png.readUInt32BE(20);
  expect(png.length, `${name} size`).toBeLessThan(MAX_PNG_BYTES);
  await testInfo.attach(name, { path: filePath, contentType: 'image/png' });
  return { file: path.basename(filePath), width, height, bytes: png.length };
}

test('RC-1 overview pipeline pre-steps (real)', async ({ page }, testInfo) => {
  await openDemoTask(page);
  await page.getByTestId('prompt-tab-overview').click();
  await expect(page.getByTestId('overview-tab')).toBeVisible();
  const pipeline = page.getByTestId('overview-pipeline');
  await expect(pipeline).toBeVisible({ timeout: 30_000 });
  await expect(pipeline.getByTestId('overview-pipeline-steps')).toBeVisible({ timeout: 30_000 });

  const groupsBefore = await pipeline.getByTestId('overview-pipeline-group').evaluateAll((nodes) =>
    nodes.map((n) => ({
      phase: n.getAttribute('data-phase'),
      label: n.querySelector('.ov-pl-phase__label')?.textContent?.trim() ?? null,
      collapsed: n.getAttribute('data-collapsed') === 'true',
      summary: n.querySelector('[data-testid="overview-pipeline-phase-summary"]')?.textContent?.replace(/\s+/g, ' ').trim() ?? null,
    })),
  );

  // Expand only the pre-step section(s) that are collapsed by default.
  const preGroups = pipeline.locator('[data-testid="overview-pipeline-group"][data-phase="pre"]');
  const preCount = await preGroups.count();
  for (let i = 0; i < preCount; i++) {
    const group = preGroups.nth(i);
    if ((await group.getAttribute('data-collapsed')) === 'true') {
      await group.getByTestId('overview-pipeline-phase').click();
      await expect(group).toHaveAttribute('data-collapsed', 'false');
    }
  }
  if (preCount > 0) {
    await expect(preGroups.first().getByTestId('overview-pipeline-step').first()).toBeVisible();
  }

  const steps = await pipeline.getByTestId('overview-pipeline-step').evaluateAll((nodes) =>
    nodes.map((n) => ({
      id: n.getAttribute('data-step-id'),
      phase: n.getAttribute('data-phase'),
      status: n.getAttribute('data-status'),
      label: n.querySelector('[data-testid="overview-pipeline-step-name"]')?.textContent?.trim() ?? null,
      hint: n.querySelector('[data-testid="overview-pipeline-skip-hint"]')?.textContent?.trim() ?? null,
      visible: (n as HTMLElement).offsetParent !== null,
    })),
  );
  const preSteps = steps.filter((s) => s.phase === 'pre');
  const promptEnrichmentRow = page
    .getByTestId('overview-pipeline-step')
    .filter({ has: page.getByTestId('overview-pipeline-step-name').getByText('Prompt enrichment', { exact: true }) });
  const promptEnrichmentVisible = await promptEnrichmentRow.first().isVisible().catch(() => false);

  // Clip to the pipeline heading through the end of the last pre-step group so
  // the labels stay legible; fall back to the whole block without a pre group.
  await pipeline.scrollIntoViewIfNeeded();
  const shotTarget = preCount > 0 ? preGroups.last() : pipeline;
  await shotTarget.scrollIntoViewIfNeeded();

  const shots = [];
  for (const theme of THEMES) {
    await setTheme(page, theme);
    const pipelineBox = await pipeline.boundingBox();
    const targetBox = await shotTarget.boundingBox();
    expect(pipelineBox).not.toBeNull();
    expect(targetBox).not.toBeNull();
    const filePath = path.join(OUT, `task-overview-pipeline-pre-steps-${theme}--real.png`);
    // The name column truncates labels; hovering the Prompt enrichment name opens
    // its explanation tooltip, which shows the full step name in the capture.
    await page.mouse.move(0, 0);
    await promptEnrichmentRow.first().getByTestId('overview-pipeline-step-name').hover();
    await page.waitForTimeout(900);
    facts['tooltipText'] = await page.locator('[role="tooltip"], .cdk-overlay-pane, [class*="tooltip"]').evaluateAll((nodes) =>
      nodes.map((n) => (n as HTMLElement).innerText.replace(/\s+/g, ' ').trim()).filter((t) => t.length > 0));
    await settle(page);
    const top = Math.max(0, pipelineBox!.y);
    const bottom = Math.min(1000, targetBox!.y + targetBox!.height + 200);
    const png = await page.screenshot({
      path: filePath,
      animations: 'disabled',
      caret: 'hide',
      clip: { x: Math.max(0, pipelineBox!.x - 260), y: top, width: pipelineBox!.width + Math.min(260, pipelineBox!.x), height: Math.max(40, bottom - top) },
    });
    expect(png.length).toBeLessThan(MAX_PNG_BYTES);
    await testInfo.attach(path.basename(filePath), { path: filePath, contentType: 'image/png' });
    shots.push({ file: path.basename(filePath), width: png.readUInt32BE(16), height: png.readUInt32BE(20), bytes: png.length, theme });
  }

  facts['pipeline'] = {
    groupsBeforeExpand: groupsBefore,
    preGroupsExpanded: preCount,
    steps,
    preStepLabelsInOrder: preSteps.map((s) => s.label),
    preStepIdsInOrder: preSteps.map((s) => s.id),
    promptEnrichmentRowVisible: promptEnrichmentVisible,
    shots,
  };
});

test('RC-1 docs tab files (real)', async ({ page }, testInfo) => {
  await openDemoTask(page);
  await page.getByTestId('prompt-tab-description').click();
  await expect(page.getByTestId('prompt-tab-description')).toContainText('Docs');
  const filesPane = page.getByTestId('files-pane');
  await expect(filesPane).toBeVisible({ timeout: 30_000 });
  const cards = filesPane.locator('article[data-testid^="file-card-"]');

  const files = await cards.evaluateAll((nodes) =>
    nodes.map((n) => ({
      testid: n.getAttribute('data-testid'),
      name: (n.getAttribute('data-testid') ?? '').replace(/^file-card-/, ''),
      kind: n.getAttribute('data-doc-kind'),
      topic: n.querySelector('[data-testid="file-card-topic"]')?.textContent?.trim() ?? null,
      head: n.querySelector('.file-card__head')?.textContent?.replace(/\s+/g, ' ').trim() ?? null,
    })),
  );
  const indexLinks = await filesPane
    .locator('[data-testid^="docs-anchor-"]')
    .evaluateAll((nodes) => nodes.map((n) => n.textContent?.trim() ?? ''));

  const body = page.getByTestId('pane-prompt-body');
  await expect(body).toBeVisible();
  await body.evaluate((el) => el.scrollTo(0, 0));

  const shots = [];
  for (const theme of THEMES) {
    await setTheme(page, theme);
    shots.push({ ...(await elementShot(page, testInfo, body, `task-docs-tab-files-${theme}--real`)), theme });
  }

  facts['docs'] = {
    tabLabel: (await page.getByTestId('prompt-tab-description').textContent())?.replace(/\s+/g, ' ').trim(),
    indexLinks,
    files,
    shots,
  };
});
