import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

const PROJECT = 'Dossier Search Evidence';
const WATCH_PATH = 'C:/evidence/dossier-search';
const DOSSIER_ID = 'orchestrator-waechter';
const DOSSIER_KEY = 'AGT-W15';
const DOSSIER_TITLE = 'Orchestrator watcher';
const DOSSIER_SUMMARY = 'Watch the runner loop and surface stalled runs.';

// Route mocks are the complete backend for this contract. A production build's
// service worker would bypass page.route after activation.
test.use({ serviceWorkers: 'block' });

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
  escalated: [], review: [], completed: [], archive: [],
};

const DOSSIER_ITEM = {
  id: DOSSIER_ID,
  key: DOSSIER_KEY,
  title: DOSSIER_TITLE,
  summary: DOSSIER_SUMMARY,
  status: 'decision-pending',
  phase: 'decision-ready',
  updatedAtUtc: '2026-09-01T10:00:00Z',
  entryPath: `docs/operations/${DOSSIER_ID}/index.html`,
  valid: true,
  error: null,
  sourceTaskKeys: [],
  relatedTaskKeys: [],
  openDecisionCount: 1,
};

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installMocks(page: Page): Promise<void> {
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route =>
    json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null }));
  await page.route('**/api/watch-paths', route =>
    json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]));
  await page.route('**/api/workspaces**', route =>
    json(route, [{
      id: 'ws-dossier-search', displayName: 'Evidence', sortOrder: 0, isDefault: true,
      projects: [{
        id: 'project-dossier-search', displayName: PROJECT, shortCode: 'AGT',
        workspaceId: 'ws-dossier-search', storageLocation: WATCH_PATH,
        sortOrder: 0, archived: false, urls: [],
      }],
    }]));
  await page.route('**/api/environment**', route =>
    json(route, { isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }));
  await page.route('**/api/runner/status**', route => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', route =>
    json(route, { at: '2026-09-01T10:00:00Z', snapshots: [], ttlSeconds: 600 }));
  await page.route('**/api/cli/usage**', route => json(route, { at: '2026-09-01T10:00:00Z', sessions: [] }));
  await page.route('**/api/crash-recovery/pending**', route => json(route, { pending: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route('**/api/tasks/grouped**', route => json(route, EMPTY_GROUPED));

  // The Dossier domain the backend now answers: no repository path, only the
  // viewer route (project plus Dossier id).
  await page.route('**/api/search?**', route => json(route, {
    query: DOSSIER_KEY,
    tasks: [], commits: [], files: [],
    dossiers: [{
      domain: 'dossiers', projectName: PROJECT, projectColor: '#569cd6',
      title: DOSSIER_TITLE, subtitle: 'decision-pending · decision-ready',
      dossierKey: DOSSIER_KEY, dossierId: DOSSIER_ID, summary: DOSSIER_SUMMARY,
    }],
    errors: {}, durationMs: 6,
  }));

  await page.route(new RegExp(`/api/projects/${encodeURIComponent(PROJECT)}/workbenches(?:\\?.*)?$`), route =>
    json(route, { projectName: PROJECT, includesHistory: false, count: 1, items: [DOSSIER_ITEM] }));
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${DOSSIER_ID}`,
    route => json(route, {
      workbench: DOSSIER_ITEM,
      html: `<!doctype html><html><body><main><h1 data-testid="dossier-heading">${DOSSIER_TITLE}</h1></main></body></html>`,
      branch: 'develop',
      revision: '1234567890abcdef',
      workingTreeModified: false,
      fingerprint: 'a'.repeat(64),
    }));
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${DOSSIER_KEY}/references`,
    route => json(route, {
      projectName: PROJECT, workbenchKey: DOSSIER_KEY, workbenchId: DOSSIER_ID,
      legacyTaskKeys: [], items: [],
    }));
}

/** Review evidence lands in the task results folder when the runner sets it. */
const SCREENSHOT_DIR = process.env.GLOBAL_SEARCH_DOSSIER_SCREENSHOT_DIR;

async function shot(page: Page, name: string): Promise<void> {
  if (!SCREENSHOT_DIR) return;
  mkdirSync(SCREENSHOT_DIR, { recursive: true });
  await page.screenshot({ path: resolve(SCREENSHOT_DIR, `${name}.png`), fullPage: true });
}

async function openDossierGroup(page: Page, theme: 'light' | 'dark') {
  await page.addInitScript(chosen => localStorage.setItem('atp.studio.theme', chosen), theme);
  await page.goto('/');
  await page.getByTestId('studio-global-search-trigger').dispatchEvent('click');
  const input = page.getByTestId('global-search-input');
  await expect(input).toHaveAttribute('placeholder', 'Search tasks, dossiers, commits, files…');
  await input.fill(DOSSIER_KEY);

  const group = page.getByTestId('global-search-group-dossiers');
  await expect(group).toContainText(DOSSIER_TITLE);
  await expect(group).toContainText(DOSSIER_KEY);
  await expect(group).toContainText('decision-pending · decision-ready');
  await expect(group).toContainText(DOSSIER_SUMMARY);
  return group;
}

test('a dossier key in the global palette opens the dossier viewer', async ({ page }) => {
  await installMocks(page);
  await openDossierGroup(page, 'light');
  await shot(page, 'global-search-dossiers-light');

  // Enter, not a click: a backendless serve can raise the "Backend not
  // reachable" overlay, which intercepts pointer events on the palette.
  await page.keyboard.press('Enter');

  await expect(page.getByTestId('global-search-input')).toHaveCount(0);
  await expect(page.getByTestId('workbench-viewer-frame')).toBeVisible({ timeout: 30_000 });
  await expect(
    page.frameLocator('[data-testid="workbench-viewer-frame"]').getByTestId('dossier-heading'),
  ).toHaveText(DOSSIER_TITLE);
  await shot(page, 'global-search-dossier-viewer');
});

test('the dossier result group renders in the dark theme', async ({ page }) => {
  await installMocks(page);
  await openDossierGroup(page, 'dark');
  await shot(page, 'global-search-dossiers-dark');
});
