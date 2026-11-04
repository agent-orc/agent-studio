import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * In-product concept docs: the small "i" trigger next to a panel title
 * opens a popover with a short paragraph plus a "Learn more" link to the
 * matching doc under docs/. The component is wired into three panels:
 * the global orchestrator card (concept = orchestrator), the project
 * supervisor section (concept = supervisor), and the project security panel
 * (concept = audits-and-checks).
 *
 * The spec verifies that each trigger renders, opens the popover with a
 * non-empty body and a Learn-more link pointing to the expected docs path,
 * and closes again when the user hits Escape.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.CONCEPT_HELP_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'concept-help');
})();

let projectName = '';
let projectSlug = '';

// Mirror of ProjectShell `toProjectSlug` (project-shell.config.ts). The
// public shell hash contract is `#/projects/<slug>[/<rail>]`, parsed by
// ProjectOverlaysService.syncShellFromHash and reload-safe on app boot.
function toProjectSlug(name: string): string {
  return name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  const preferred = paths.find(p => /agent.?task/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
  projectSlug = toProjectSlug(projectName);
});

// Deep-link straight to a project-shell rail. The studio-shell refactor
// removed the per-project `project-shell-open-<name>` button from the `/`
// landing board, so navigation goes through the hash contract instead.
async function openProjectRail(
  page: import('@playwright/test').Page,
  rail: string,
) {
  await page.goto(`/#/projects/${projectSlug}/${rail}`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
}

async function assertConceptHelp(
  page: import('@playwright/test').Page,
  concept: string,
  expectedDocsPath: string,
) {
  const trigger = page.getByTestId(`concept-help-trigger-${concept}`);
  await expect(trigger).toBeVisible();
  await trigger.click();

  const popover = page.getByTestId(`concept-help-popover-${concept}`);
  await expect(popover).toBeVisible();

  const body = page.getByTestId(`concept-help-body-${concept}`);
  const bodyText = (await body.textContent())?.trim() ?? '';
  expect(bodyText.length).toBeGreaterThan(40);

  const learn = page.getByTestId(`concept-help-learn-${concept}`);
  await expect(learn).toBeVisible();
  const href = await learn.getAttribute('href');
  expect(href).toContain(expectedDocsPath);

  const pathChip = page.getByTestId(`concept-help-path-${concept}`);
  await expect(pathChip).toContainText(expectedDocsPath);

  // Close via Escape and confirm the popover unmounts.
  await page.keyboard.press('Escape');
  await expect(popover).toHaveCount(0);
}

test('orchestrator concept-help on the global orchestrator card', async ({ page }) => {
  // The global orchestrator card lives under the Orchestrator rail of the
  // project shell (see project-detail.html `view === 'orchestrator'`).
  await openProjectRail(page, 'orchestrator');
  const card = page.getByTestId('global-orchestrator-card');
  await card.scrollIntoViewIfNeeded();
  await expect(card).toBeVisible({ timeout: 10_000 });
  await card.getByTestId('global-orchestrator-toggle').click();
  await assertConceptHelp(page, 'orchestrator', 'docs/system/architecture/decisions/adr-archive.md');
  await page.screenshot({
    path: `${SCREENSHOT_DIR}/01-orchestrator.png`,
    fullPage: true,
  });
});

test('supervisor concept-help on the project supervisor section', async ({ page }) => {
  // The supervisor section lives under the Observability rail (see
  // project-detail.html `view === 'observability'`).
  await openProjectRail(page, 'observability');
  const section = page.getByTestId('project-supervisor-section');
  await section.scrollIntoViewIfNeeded();
  await expect(section).toBeVisible();
  await assertConceptHelp(page, 'supervisor', 'docs/system/architecture/decisions/adr-archive.md');
  await page.screenshot({
    path: `${SCREENSHOT_DIR}/02-supervisor.png`,
    fullPage: true,
  });
});

test('audits-and-checks concept-help on the project security panel', async ({ page }) => {
  await openProjectRail(page, 'security');
  await expect(page.getByTestId('security-panel')).toBeVisible();
  await assertConceptHelp(page, 'audits-and-checks', 'docs/operations/security/overview.md');
  await page.screenshot({
    path: `${SCREENSHOT_DIR}/03-audits-and-checks.png`,
    fullPage: true,
  });
});
