import { test, expect, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme, type Theme } from '../helpers/theme';

/**
 * Deterministic 16:9 presentation stills over the isolated ADR-0056 demo
 * workspace. `scripts/presentation-capture/generate.sh` owns stack lifecycle,
 * demo reset, and marketing mode. A 1920x1080 CSS viewport at DSF 2 produces
 * 3840x2160 PNGs that stay crisp when scaled into a 1920x1080 slide.
 */
test.describe.configure({ mode: 'serial' });
test.use({
  viewport: { width: 1920, height: 1080 },
  deviceScaleFactor: 2,
  reducedMotion: 'reduce',
});

const OUT = path.resolve(process.cwd(), '../docs/assets/images/presentation');
const TASK_CARD = '[data-testid="task-card"], [data-testid="job-card"]';
const DEMO_APP = 'Demo App';
const FIXED_BROWSER_TIME = '2026-08-09T13:00:00.000Z';
const ANNOTATIONS_ENABLED = !['0', 'false', 'off'].includes(
  (process.env.PW_PRESENTATION_ANNOTATIONS ?? '1').trim().toLowerCase(),
);

interface ShotAnnotation {
  label: string;
  left: number;
  top: number;
}

const BOARD_HERO_ANNOTATIONS: readonly ShotAnnotation[] = [
  { label: 'Pinned workspace', left: 16, top: 14 },
  { label: 'Bounded task', left: 24, top: 31 },
  { label: 'Human decision', left: 80, top: 31 },
];

const TASK_DETAIL_ANNOTATIONS: readonly ShotAnnotation[] = [
  { label: 'Review verdict', left: 18, top: 34 },
  { label: 'Evidence attached', left: 33, top: 68 },
  { label: 'Human decision', left: 74, top: 48 },
];

test.beforeAll(() => {
  fs.rmSync(OUT, { recursive: true, force: true });
  fs.mkdirSync(OUT, { recursive: true });
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
      [data-testid="status-bar"] .statusbar__quota {
        visibility: hidden !important;
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

async function shot(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  const filePath = path.join(OUT, `${name}.png`);
  await settle(page);
  const png = await page.screenshot({
    path: filePath,
    fullPage: false,
    animations: 'disabled',
    caret: 'hide',
    scale: 'device',
  });
  expect(png.readUInt32BE(16), `${name} width`).toBe(3840);
  expect(png.readUInt32BE(20), `${name} height`).toBe(2160);
  await testInfo.attach(name, { path: filePath, contentType: 'image/png' });
}

async function captureBoth(
  page: Page,
  testInfo: TestInfo,
  order: string,
  name: string,
  annotations: readonly ShotAnnotation[] = [],
): Promise<void> {
  for (const theme of ['dark', 'light'] as const satisfies readonly Theme[]) {
    await setTheme(page, theme);
    await renderAnnotations(page, annotations);
    await shot(page, testInfo, `${order}-${name}--${theme}--pinned`);
  }
}

async function renderAnnotations(page: Page, annotations: readonly ShotAnnotation[]): Promise<void> {
  expect(annotations.length, 'presentation annotations per shot').toBeLessThanOrEqual(3);
  await page.locator('[data-presentation-annotations]').evaluateAll((nodes) => {
    for (const node of nodes) node.remove();
  });
  if (!ANNOTATIONS_ENABLED || annotations.length === 0) return;

  await page.evaluate((labels) => {
    const root = document.createElement('div');
    root.dataset['presentationAnnotations'] = 'true';
    root.setAttribute('aria-label', 'Presentation annotations');
    Object.assign(root.style, {
      position: 'fixed',
      inset: '0',
      zIndex: '2147483647',
      pointerEvents: 'none',
    });
    for (const annotation of labels) {
      const label = document.createElement('div');
      label.dataset['presentationAnnotation'] = annotation.label;
      label.textContent = annotation.label;
      Object.assign(label.style, {
        position: 'absolute',
        left: `${annotation.left}%`,
        top: `${annotation.top}%`,
        display: 'inline-flex',
        alignItems: 'center',
        border: '1px solid var(--studio-border-strong)',
        borderRadius: '999px',
        background: 'var(--studio-bg-elevated)',
        color: 'var(--studio-fg-strong)',
        padding: '10px 16px',
        font: '600 16px/1.2 system-ui',
        letterSpacing: '0.01em',
      });
      const dot = document.createElement('span');
      dot.setAttribute('aria-hidden', 'true');
      Object.assign(dot.style, {
        width: '8px',
        height: '8px',
        marginRight: '9px',
        borderRadius: '50%',
        background: 'var(--studio-accent)',
      });
      label.prepend(dot);
      root.append(label);
    }
    document.body.append(root);
  }, annotations);
}

async function freezeBrowserClock(page: Page): Promise<void> {
  await page.clock.setFixedTime(FIXED_BROWSER_TIME);
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
  await freezeBrowserClock(page);
  await page.goto('/');
  await applyMarketingMode(page);
  await dismissBlockingOverlays(page);
  const trigger = page.getByTestId('studio-project-picker-trigger');
  await expect(trigger).toBeVisible({ timeout: 15_000 });
  await trigger.click();
  await page.getByTestId('studio-project-picker-panel').getByText(DEMO_APP, { exact: true }).click();
  await expect(page.locator(TASK_CARD).filter({ hasText: 'DEMO-9' }).first()).toBeVisible();
}

async function openDemoTask(page: Page): Promise<void> {
  await openBoard(page);
  const card = page.locator(TASK_CARD).filter({ hasText: 'DEMO-5' }).first();
  await card.scrollIntoViewIfNeeded();
  await card.click();
  await expect(page.getByTestId('pane-protocol')).toBeVisible();
}

async function openDecisionTask(page: Page): Promise<void> {
  await openBoard(page);
  const card = page.locator(TASK_CARD).filter({ hasText: 'DEMO-9' }).first();
  await card.scrollIntoViewIfNeeded();
  await card.click();
  await expect(page.getByTestId('escalation-summary')).toBeVisible();
  await expect(page.getByTestId('escalation-recommendation')).toContainText('Needs decision');
}

async function openProjectHub(page: Page): Promise<void> {
  await openBoard(page);
  const row = page.getByTestId(`studio-explorer-project-${DEMO_APP}`);
  await expect(row).toBeVisible();
  await row.getByRole('button', { name: 'Open Deck' }).click();
  await expect(page.getByTestId('project-shell-rail-token-usage')).toBeVisible({ timeout: 60_000 });
}

test('presentation still 01 - cross-lane board', async ({ page }, testInfo) => {
  await openBoard(page);
  await captureBoth(page, testInfo, '01', 'board-overview');
});

test('presentation still 02 - task execution detail', async ({ page }, testInfo) => {
  await openDemoTask(page);
  await captureBoth(page, testInfo, '02', 'task-execution-detail');
});

test('presentation still 03 - review evidence', async ({ page }, testInfo) => {
  await openDemoTask(page);
  await page.getByTestId('prompt-tab-code-review').click();
  const review = page.getByRole('button', { name: /Export flow is ready for human review/ });
  await expect(review).toBeVisible();
  await review.click();
  await expect(page.getByTestId('code-review-body')).toContainText('Add an assertion for exporting an empty report table');
  await captureBoth(page, testInfo, '03', 'review-evidence');
});

test('presentation still 04 - orchestrator conversation', async ({ page }, testInfo) => {
  await openBoard(page);
  await page.getByTestId('studio-titlebar-chat').click();
  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
  await expect(page.getByTestId('orchestrator-conversation')).toContainText('MVP walkthrough');
  await captureBoth(page, testInfo, '04', 'orchestrator-conversation');
});

test('presentation still 05 - token economy', async ({ page }, testInfo) => {
  await openProjectHub(page);
  await page.getByTestId('project-shell-rail-token-usage').click();
  await expect(page.getByTestId('project-token-usage-panel')).toBeVisible({ timeout: 60_000 });
  await captureBoth(page, testInfo, '05', 'token-economy');
});

test('presentation still 06 - project knowledge', async ({ page }, testInfo) => {
  await openProjectHub(page);
  await page.getByTestId('project-shell-rail-wiki').click();
  await expect(page.getByTestId('project-shell-panel-wiki')).toBeVisible({ timeout: 60_000 });
  const firstFile = page.locator('[data-testid^="project-wiki-file-"]').first();
  await expect(firstFile).toBeVisible();
  await firstFile.click();
  await expect(page.getByTestId('project-wiki-viewer').first()).toBeVisible();
  await captureBoth(page, testInfo, '06', 'project-knowledge');
});

test('presentation still 07 - landing board hero', async ({ page }, testInfo) => {
  await openBoard(page);
  const decisionCard = page.locator(TASK_CARD).filter({ hasText: 'DEMO-9' }).first();
  await expect(decisionCard).toBeInViewport();
  await expect(page.getByTestId('lane-5e-escalated').locator(TASK_CARD).first()).toContainText('DEMO-9');
  await expect(page.getByTestId('accepted-integration-alert-banner')).toBeHidden();
  await captureBoth(page, testInfo, '07', 'landing-board-hero', BOARD_HERO_ANNOTATIONS);
});

test('presentation still 08 - landing task decision detail', async ({ page }, testInfo) => {
  await openDecisionTask(page);
  await page.getByTestId('prompt-tab-evidence').click();
  await expect(page.getByTestId('evidence-view')).toBeVisible();
  await expect(page.getByTestId('screenshot-source').filter({ hasText: 'real' })).toBeVisible();
  await page.getByTestId('inspector-tab-protocol').click();
  await expect(page.getByTestId('decision-surface')).toBeVisible();
  await expect(page.getByTestId('decision-surface-title')).toContainText('Release the reports export?');
  await captureBoth(page, testInfo, '08', 'landing-task-detail', TASK_DETAIL_ANNOTATIONS);
});

// AGT-2530 owns the ADR-0056 shot sequence. This focused hook captures the
// gallery scene without changing the existing board and decision shots.
test('presentation still 08c - activity artifact gallery', async ({ page }, testInfo) => {
  await openDemoTask(page);
  await page.getByTestId('inspector-tab-activity').click();
  const gallery = page.getByTestId('conversation-artifact-gallery');
  await expect(gallery).toBeVisible();
  await expect(gallery.getByTestId('artifact-gallery-thumbnail')).toHaveCount(8);
  await expect(gallery.getByTestId('artifact-document-diff')).toBeVisible();
  await expect(gallery.getByTestId('artifact-document-markdown')).toBeVisible();
  await gallery.scrollIntoViewIfNeeded();
  await captureBoth(page, testInfo, '08c', 'activity-artifact-gallery');
});
