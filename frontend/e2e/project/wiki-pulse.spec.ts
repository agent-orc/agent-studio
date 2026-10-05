import { test, expect } from '../fixtures/dev-backend';
import * as fs from 'fs';
import * as path from 'path';
import { BACKEND } from '../helpers/api';

/**
 * Wiki Pulse landing view (PULSE-1). The wiki opens on the generated Pulse view
 * (change feed + inbox + drift grade bar), not a page. This spec drives the real
 * app but overlays a deterministic `/wiki/pulse` payload via route interception
 * (labelled --mocked) so the assertions and the screenshot are stable regardless
 * of the picked project's live git state. Navigation uses the deep-link hash
 * contract (`#/projects/<slug>/wiki`).
 *
 * Screenshots land in the orchestrator job results dir when
 * PROJECT_WIKI_RESULTS_DIR is set; otherwise a sibling of the spec.
 */

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.PROJECT_WIKI_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'wiki-pulse');
})();

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

const PULSE_FIXTURE = {
  projectName: 'demo',
  baseDir: '/repo/docs',
  exists: true,
  generatedAtUtc: '2026-07-10T09:00:00Z',
  feed: {
    available: true,
    reason: null,
    items: [
      {
        relPath: 'concepts/pulse.md',
        title: 'Pulse landing view', author: 'Robert Mischke',
        authorDateUtc: new Date(Date.now() - 2 * 3600_000).toISOString(),
        sha: 'aaaaaaa', shortSha: 'aaaaaaa', subject: 'AGT-2014 land pulse',
        areaSlug: 'concepts', areaTitle: 'concepts',
        taskKey: 'AGT-2014',
      },
      {
        relPath: 'architecture/relocation.md',
        title: 'Wiki checkout relocation', author: 'Claude Opus 4.8',
        authorDateUtc: new Date(Date.now() - 26 * 3600_000).toISOString(),
        sha: 'bbbbbbb', shortSha: 'bbbbbbb', subject: 'AGT-1984 relocate wiki',
        areaSlug: 'architecture', areaTitle: 'architecture',
        taskKey: 'AGT-1984',
      },
    ],
  },
  inbox: {
    available: true,
    reason: null,
    count: 1,
    items: [
      { relPath: 'scratch-idea.md', title: 'Scratch idea', type: 'md', reason: 'Loose page at the wiki root - not filed under a category.' },
    ],
  },
  drift: {
    available: true,
    reason: null,
    overallGrade: 'Stale',
    areas: [
      { slug: 'concepts', title: 'concepts', grade: 'Aging', pageCount: 2, gradedPageCount: 2, worstCommitCount: 12, freshCount: 1, agingCount: 1, staleCount: 0 },
      { slug: 'operations', title: 'operations', grade: 'Fresh', pageCount: 1, gradedPageCount: 1, worstCommitCount: 2, freshCount: 1, agingCount: 0, staleCount: 0 },
      { slug: 'architecture', title: 'architecture', grade: 'Stale', pageCount: 1, gradedPageCount: 1, worstCommitCount: 73, freshCount: 0, agingCount: 0, staleCount: 1 },
    ],
    counts: { fresh: 2, aging: 1, stale: 1, graded: 4 },
  },
  critical: {
    available: true,
    reason: null,
    count: 2,
    overallGrade: 'D',
    items: [
      { relPath: 'architecture/relocation.md', title: 'Wiki checkout relocation', grade: 'D', assessment: 'Describes an old checkout layout; likely outdated.', gradedAt: '2026-07-10T09:00:00Z', model: 'claude-sonnet-5', reportPath: 'architecture/relocation.md.report.html', areaTitle: 'architecture' },
      { relPath: 'scratch-idea.md', title: 'Scratch idea', grade: 'C', assessment: 'Thin, unfiled scratch note with gaps.', gradedAt: '2026-07-10T09:00:00Z', model: 'claude-sonnet-5', reportPath: 'scratch-idea.md.report.html', areaTitle: null },
    ],
  },
  warnings: {
    available: true,
    reason: null,
    count: 2,
    items: [
      { kind: 'human-action', title: 'Runner restart loop', detail: 'Development signal is active.', humanAction: 'Inspect the latest failed resume before reissuing.', relPath: 'operations/signals/restart.md', status: 'active' },
      { kind: 'dead-link', title: 'Dead link in operator guide', detail: '../missing-runbook.md', humanAction: 'Repair or remove this internal link.', relPath: 'operations/operator-guide.md', status: null },
    ],
  },
  activity: {
    available: true,
    reason: null,
    runs: [{ taskKey: 'AGT-2015', lane: '3-progress', startedAtUtc: new Date(Date.now() - 43 * 60_000).toISOString(), docsFilesChanged: 3 }],
  },
  lifecycle: {
    available: true,
    reason: null,
    count: 4,
    items: [
      {
        relPath: 'concepts/tree-project-indicator-alternatives.md', title: 'Project State Indicator Alternatives',
        pageKind: 'exploration', state: 'review-requested', editedBy: 'Robert Mischke',
        editedAtUtc: new Date(Date.now() - 45 * 60_000).toISOString(), history: [], workbenchId: null,
        valid: true, error: null,
      },
      {
        relPath: 'quality/architecture-quality-layer/index.html', title: 'Architecture and quality layer',
        pageKind: 'workbench', state: 'review-requested', editedBy: 'AGT-2137',
        editedAtUtc: new Date(Date.now() - 60 * 60_000).toISOString(), history: [], workbenchId: 'architecture-quality-layer',
        valid: true, error: null,
      },
      {
        relPath: 'concepts/experimentier-workbench.md', title: 'Experiment workbenches',
        pageKind: 'concept', state: 'in-progress', editedBy: 'AGT-2137',
        editedAtUtc: new Date(Date.now() - 2 * 3600_000).toISOString(), history: [], workbenchId: null,
        valid: true, error: null,
      },
      {
        relPath: 'concepts/wiki-pulse-dashboard.md', title: 'Wiki Pulse dashboard',
        pageKind: 'concept', state: 'decided', editedBy: 'AGT-2137',
        editedAtUtc: new Date(Date.now() - 3 * 3600_000).toISOString(), history: [], workbenchId: null,
        valid: true, error: null,
      },
    ],
  },
  workbenches: {
    projectName: 'demo', includesHistory: false, count: 1,
    items: [{
      id: 'architecture-quality-layer', title: 'Architecture and quality layer', summary: 'Review the layer.',
      status: 'active', phase: 'decision-ready', updatedAtUtc: new Date(Date.now() - 60 * 60_000).toISOString(),
      entryPath: 'quality/architecture-quality-layer/index.html', valid: true, error: null, sourceTaskKeys: ['AGT-2126'],
      lifecycleState: 'review-requested', editedBy: 'AGT-2137', lifecycleHistory: [],
    }],
  },
};

const MAINTENANCE_MODEL = { cliType: 'claude', model: 'claude-sonnet-5', thinkingLevel: null };
const GRADING_STATUS_DONE = {
  status: {
    projectName: 'demo', runId: 'wg-e2e', state: 'completed', cliType: 'claude', model: 'claude-sonnet-5',
    thinkingLevel: null, force: false, total: 12, processed: 12, graded: 10, skipped: 2, failed: 0,
    critical: 2, currentRelPath: null, startedAtUtc: '2026-07-10T09:00:00Z', completedAtUtc: '2026-07-10T09:02:00Z',
    error: null, recent: [],
  },
};
const STYLE_GUIDES_FIXTURE = {
  projectKey: 'PROJ-0042',
  projectDisplayName: 'Demo',
  technologies: [
    { key: 'angular', displayLabel: 'Angular' },
    { key: 'dotnet', displayLabel: '.NET' },
  ],
  guides: [
    {
      id: 'angular-components', title: 'Angular component guide', relPath: 'quality/angular-components.md',
      summary: 'Rendering, identity, and token rules for Angular UI work.',
      promptSummary: 'Use standalone OnPush components and semantic tokens.', version: '1',
      appliesTo: { projects: ['*'], technologies: ['angular'], taskAreas: ['frontend'] },
      match: {
        projectWildcard: true, projectSelector: '*', technologyWildcard: false,
        technologies: [{ key: 'angular', displayLabel: 'Angular' }],
      },
    },
    {
      id: 'dotnet-backend', title: '.NET backend guide', relPath: 'quality/dotnet-backend.md',
      summary: 'Feature ownership, pure policy, and side-effect ordering for .NET work.',
      promptSummary: 'Keep feature flow explicit and policy pure.', version: '1',
      appliesTo: { projects: ['*'], technologies: ['dotnet'], taskAreas: ['backend'] },
      match: {
        projectWildcard: true, projectSelector: '*', technologyWildcard: false,
        technologies: [{ key: 'dotnet', displayLabel: '.NET' }],
      },
    },
  ],
  warnings: [],
  snapshotId: '0123456789abcdef',
  capturedAtUtc: '2026-07-14T08:00:00Z',
  refreshAfterUtc: '2026-07-14T08:05:00Z',
};

/** Mocks the grading trigger's seed endpoints so the surface is deterministic. */
async function mockGradingContext(page: import('@playwright/test').Page): Promise<void> {
  await page.route('**/api/cli/maintenance-model', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(MAINTENANCE_MODEL) }));
  await page.route('**/wiki/grading/status**', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GRADING_STATUS_DONE) }));
}

/** Keeps the spec independent of the host's configured real projects. */
async function mockProjectContext(page: import('@playwright/test').Page): Promise<void> {
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
  await page.route('**/api/crash-recovery/pending', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ pending: [] }),
  }));
  await page.route('**/api/watch-paths', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{ name: 'demo', path: '/throwaway/demo', rootPath: '/throwaway/demo' }]),
  }));
  await page.route('**/api/projects/demo/wiki/tree', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ exists: true, root: [{ type: 'md', name: 'README.md', title: 'Readme', relPath: 'README.md', children: [] }] }),
  }));
  await page.route('**/api/projects/demo/style-guides', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(STYLE_GUIDES_FIXTURE),
  }));
}

/**
 * A route callback that rejects fails the test it belongs to. The app keeps
 * polling in the background (e.g. `GET /api/cli/claude/models`), so a proxied
 * request can still be mid-flight when a short test's assertions finish and
 * Playwright tears the browser context down underneath it. `route.fetch` then
 * rejects with "Target page, context or browser has been closed" — which is the
 * sporadic failure that only bit the *fast* combined-run scenarios (empty-state,
 * grading) while the slower landing test outlived its own poll. Treat these
 * teardown-race errors as benign; they are not a product defect.
 */
function isTeardownRace(error: unknown): boolean {
  const message = (error instanceof Error ? error.message : String(error)).toLowerCase();
  return message.includes('has been closed')
    || message.includes('target closed')
    || message.includes('disposed')
    || message.includes('test ended');
}

async function proxyBackend(page: import('@playwright/test').Page): Promise<void> {
  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const target = `${BACKEND}${url.pathname}${url.search}`;
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        const response = await route.fetch({ url: target });
        await route.fulfill({ response });
        return;
      } catch (error) {
        // Never throw out of a route handler. A closing context is expected at
        // teardown; a genuinely failing hop is aborted so the *assertion* (not
        // this callback) surfaces the cause with a readable message.
        if (isTeardownRace(error)) return;
        if (attempt === 2) {
          console.warn(`proxyBackend ${request.method()} ${url.pathname} failed: ${error instanceof Error ? error.message : error}`);
          await route.abort('failed').catch(() => { /* context already gone */ });
          return;
        }
        await new Promise(resolve => setTimeout(resolve, 100));
      }
    }
  });
}

test.describe('Wiki Pulse landing view (PULSE-2)', () => {
  const projectName = 'demo';

  test.afterEach(async ({ page }) => {
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  });

  test.beforeAll(() => {
    fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  });

  test('opens on the generated Pulse view with feed, inbox, and drift grade bar', async ({ page, devBackend }) => {
    expect(devBackend.port).toBe(5030);
    await proxyBackend(page);
    await mockProjectContext(page);
    // Overlay a deterministic Pulse payload so the surface is stable (--mocked).
    await page.route('**/wiki/pulse**', route =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(PULSE_FIXTURE) }));
    await mockGradingContext(page);

    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);

    const pulse = page.getByTestId('project-wiki-pulse');
    await expect(pulse).toBeVisible({ timeout: 10_000 });

    // Drift grade bar: real top-level docs folders, worst grade elevated to the overall chip.
    await expect(page.getByTestId('project-wiki-pulse-overall')).toContainText('Stale');
    await expect(page.getByTestId('project-wiki-pulse-area-architecture')).toContainText('Stale');
    await expect(page.getByTestId('project-wiki-pulse-area-operations')).toContainText('Fresh');

    // Change feed: top-folder badge + task key on a row.
    await expect(page.getByTestId('project-wiki-pulse-task-concepts/pulse.md'))
      .toContainText('AGT-2014');
    await expect(page.getByTestId('project-wiki-pulse-area-badge-concepts/pulse.md'))
      .toContainText('concepts');

    // Inbox: an unfiled root page needing sorting.
    await expect(page.getByTestId('project-wiki-pulse-inbox-open-scratch-idea.md')).toBeVisible();
    await expect(page.getByTestId('project-wiki-pulse-inbox')).toContainText('Needs sorting');

    await expect(page.getByTestId('project-wiki-pulse-warnings')).toContainText('Inspect the latest failed resume');
    await expect(page.getByTestId('project-wiki-pulse-live-AGT-2015')).toContainText('3 docs files changed');

    // No horizontal overflow on the landing surface.
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
    expect(overflow).toBe(false);

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'wiki-pulse-warnings-in-progress--mocked.png'), fullPage: true });
  });

  test('shows applicable style guides and review-first lifecycle groups in both themes', async ({ page, devBackend }) => {
    expect(devBackend.port).toBe(5030);
    await proxyBackend(page);
    await mockProjectContext(page);
    await page.route('**/wiki/pulse**', route =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(PULSE_FIXTURE) }));
    await page.route('**/wiki/files/concepts/tree-project-indicator-alternatives.md', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        relPath: 'concepts/tree-project-indicator-alternatives.md',
        content: '# Project State Indicator Alternatives\n\nOptions are ready for review.',
      }),
    }));
    await page.route('**/wiki/history/concepts/tree-project-indicator-alternatives.md', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        relPath: 'concepts/tree-project-indicator-alternatives.md',
        model: null,
        metadata: {
          model: null, updatedAt: null, reason: null, taskKey: null,
          status: null, runCount: null, hasFrontmatter: true,
        },
        commits: [],
      }),
    }));
    await mockGradingContext(page);
    await page.addInitScript(() => {
      if (!localStorage.getItem('atp.studio.theme')) localStorage.setItem('atp.studio.theme', 'light');
    });

    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);

    const panel = page.getByTestId('project-wiki-style-guides');
    const lifecycle = page.getByTestId('project-wiki-pulse-lifecycle');
    await expect(panel).toBeVisible({ timeout: 10_000 });
    await expect(lifecycle).toBeVisible();
    await expect(page.getByTestId('project-wiki-lifecycle-group-review-requested'))
      .toContainText('Project State Indicator Alternatives');
    await expect(page.getByTestId('project-wiki-lifecycle-group-in-progress'))
      .toContainText('Experiment workbenches');
    await expect(page.getByTestId('project-wiki-lifecycle-group-decided'))
      .toContainText('Wiki Pulse dashboard');
    await expect(panel).toContainText('Angular component guide');
    await expect(panel).toContainText('.NET backend guide');
    await expect(panel).toContainText('Prompt context · v1');
    await expect(panel).toContainText('Snapshot 01234567');
    await expect(panel).toContainText('Matches all projects');
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'light');
    await panel.screenshot({ path: path.join(SCREENSHOT_DIR, 'style-guides-light--mocked.png') });
    await lifecycle.screenshot({ path: path.join(SCREENSHOT_DIR, 'wiki-lifecycle-light--mocked.png') });

    await page.evaluate(() => localStorage.setItem('atp.studio.theme', 'dark'));
    await page.reload();
    await expect(panel).toBeVisible({ timeout: 10_000 });
    await expect(lifecycle).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
    await panel.screenshot({ path: path.join(SCREENSHOT_DIR, 'style-guides-dark--mocked.png') });
    await lifecycle.screenshot({ path: path.join(SCREENSHOT_DIR, 'wiki-lifecycle-dark--mocked.png') });

    await page.getByTestId('project-wiki-lifecycle-open-concepts/tree-project-indicator-alternatives.md').click();
    await expect(page.getByTestId('project-wiki-viewer-path'))
      .toContainText('concepts/tree-project-indicator-alternatives.md');
  });

  test('degrades to labelled empty states when a source is unavailable', async ({ page, devBackend }) => {
    expect(devBackend.port).toBe(5030);
    await proxyBackend(page);
    await mockProjectContext(page);
    await page.route('**/wiki/pulse**', route => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        ...PULSE_FIXTURE,
        feed: { available: true, reason: 'No recent edits in git history.', items: [] },
        inbox: { available: true, reason: null, count: 0, items: [] },
        drift: { available: true, reason: 'No knowledge pages in any top-level docs folder yet.', overallGrade: 'Empty',
          areas: PULSE_FIXTURE.drift.areas.map(a => ({ ...a, grade: 'Empty', pageCount: 0, gradedPageCount: 0, worstCommitCount: 0, freshCount: 0, agingCount: 0, staleCount: 0 })),
          counts: { fresh: 0, aging: 0, stale: 0, graded: 0 } },
      }),
    }));

    await mockGradingContext(page);
    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);
    await expect(page.getByTestId('project-wiki-pulse')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('project-wiki-pulse-feed-empty')).toContainText('No recent edits');
    // A clear inbox contributes no sub-list to the merged Aufmerksamkeit card;
    // the card itself stays because the fixture's warnings remain active.
    await expect(page.getByTestId('project-wiki-pulse-attention')).toBeVisible();
    await expect(page.getByTestId('project-wiki-pulse-inbox')).toHaveCount(0);
    await expect(page.getByTestId('project-wiki-pulse-drift-empty')).toContainText('No knowledge pages in any top-level docs folder');
  });

  test('shows the grading trigger and critical pages (AGT-2051)', async ({ page, devBackend }) => {
    expect(devBackend.port).toBe(5030);
    await proxyBackend(page);
    await mockProjectContext(page);
    await page.route('**/wiki/pulse**', route =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(PULSE_FIXTURE) }));
    await mockGradingContext(page);

    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);
    await expect(page.getByTestId('project-wiki-pulse')).toBeVisible({ timeout: 10_000 });

    // The grading trigger: a model dropdown + a "Grade all pages" button.
    const grading = page.getByTestId('project-wiki-pulse-grading');
    await expect(grading).toBeVisible();
    await expect(page.getByTestId('project-wiki-pulse-grade-start')).toBeVisible();
    // The last run's outcome is summarised (10 graded, 2 critical).
    await expect(page.getByTestId('project-wiki-pulse-grade-state')).toContainText('critical');

    // Critical pages: worst-first (D before C), click-through targets present.
    const critical = page.getByTestId('project-wiki-pulse-critical');
    await expect(critical).toContainText('Critical pages');
    await expect(page.getByTestId('project-wiki-pulse-critical-open-architecture/relocation.md')).toBeVisible();
    await expect(page.getByTestId('project-wiki-pulse-critical-open-scratch-idea.md')).toBeVisible();

    // No horizontal overflow with the new sections.
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
    expect(overflow).toBe(false);

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'wiki-grading-trigger-and-critical--mocked.png'), fullPage: true });
  });
});
