import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { contrastRatio, parseRgb } from '../helpers/contrast';
import { dismissDevErrorDialog, sampleColours, setTheme } from '../helpers/theme';

const RESULTS_DIR = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'wiki-source-branch')
  : path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'wiki-source-branch');

const source = {
  mode: 'branch', branch: 'origin/develop', commit: 'abcdef1234567890',
  shortCommit: 'abcdef12', writable: false, error: null,
};

const tree = {
  projectName: 'Demo', baseDir: 'docs', exists: true, source,
  root: [{
    name: 'guides', title: 'Guides', relPath: 'guides', type: 'folder', metadata: null, children: [{
      name: 'operator.md', title: 'Operator guide', relPath: 'guides/operator.md',
      type: 'md', children: [], metadata: null, immutable: false,
    }],
  }],
};

const pulse = {
  projectName: 'Demo', baseDir: 'docs', exists: true, generatedAtUtc: '2026-07-12T03:00:00Z',
  feed: { available: true, reason: null, items: [] },
  inbox: { available: true, reason: null, count: 0, items: [] },
  drift: { available: true, reason: null, overallGrade: 'Fresh', areas: [], counts: { fresh: 1, aging: 0, stale: 0, graded: 1 } },
  critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
  warnings: { available: true, reason: null, count: 0, items: [] },
  activity: { available: true, reason: null, runs: [], collector: null, curator: null },
};

let settingsUpdate: Record<string, unknown> | null;
let releaseSettingsUpdate: (() => void) | undefined;

test.beforeEach(async ({ page }) => {
  await page.setViewportSize({ width: 1512, height: 982 });
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  settingsUpdate = null;
  releaseSettingsUpdate = undefined;
  await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill({ json: {
    connectionId: 'wiki-source-branch-e2e',
    connectionToken: 'wiki-source-branch-e2e',
    negotiateVersion: 1,
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  } }));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (message.toString().includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  await page.route('**/api/**', async route => {
    const endpoint = new URL(route.request().url()).pathname;
    if (endpoint === '/api/v1/studio/auth/status') {
      return route.fulfill({ json: {
        profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
      } });
    }
    if (endpoint === '/api/environment') {
      return route.fulfill({ json: {
        isDev: false,
        devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
      } });
    }
    if (endpoint === '/api/crash-recovery/pending') {
      return route.fulfill({ json: { pending: [] } });
    }
    if (endpoint === '/api/watch-paths') {
      return route.fulfill({ json: [{ name: 'Demo', path: '/tmp/demo/jobs', rootPath: '/tmp/demo' }] });
    }
    if (endpoint === '/api/v1/studio/board') {
      return route.fulfill({ json: {
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
        escalated: [], review: [], completed: [], archive: [],
      } });
    }
    if (endpoint === '/api/tasks/archive') {
      return route.fulfill({ json: { items: [], total: 0, offset: 0, limit: 50 } });
    }
    if (endpoint === '/api/v1/studio/runner/status') return route.fulfill({ json: { projects: {} } });
    if (endpoint === '/api/cli/quota') {
      return route.fulfill({ json: {
        at: '2026-07-12T03:00:00Z', ttlSeconds: 600, snapshots: [],
      } });
    }
    if (endpoint.startsWith('/api/bus/')) return route.fulfill({ json: [] });
    if (endpoint === '/api/v1/management/remote-hosts') return route.fulfill({ json: [] });
    if (endpoint === '/api/tags' || endpoint === '/api/clients' || endpoint === '/api/clients/') {
      return route.fulfill({ json: [] });
    }
    if (endpoint === '/api/v1/workspaces') {
      return route.fulfill({ json: [{
        id: 'ws-default', displayName: 'Workspace', sortOrder: 0, isDefault: true,
        color: null, createdAt: '2026-07-12T00:00:00Z', projects: [{
          sourceType: 'local-folder', id: 'PROJ-001', displayName: 'Demo', shortCode: 'DEM',
          workspaceId: 'ws-default', color: null, cliDefault: null, modelDefault: null,
          sortOrder: 0, storageLocation: '/tmp/demo/jobs', urls: [], archived: false,
          createdAt: '2026-07-12T00:00:00Z', wikiSourceBranch: 'origin/develop',
        }],
      }] });
    }
    if (endpoint === '/api/git/inventory') {
      return route.fulfill({ json: {
        isRepo: true, currentBranch: 'main',
        branches: [{ name: 'develop', upstream: 'origin/develop' }],
      } });
    }
    if (endpoint === '/api/projects/PROJ-001' && route.request().method() === 'PUT') {
      settingsUpdate = route.request().postDataJSON();
      await new Promise<void>(resolve => { releaseSettingsUpdate = resolve; });
      return route.fulfill({ json: { wikiSourceBranch: null } });
    }
    if (endpoint === '/api/projects/Demo/wiki/tree') return route.fulfill({ json: tree });
    if (endpoint === '/api/projects/Demo/wiki/pulse') return route.fulfill({ json: pulse });
    if (endpoint === '/api/projects/Demo/snapshot') {
      return route.fulfill({ json: {
        project: 'Demo', capturedAt: '2026-07-12T03:00:00Z',
        paths: { path: '/tmp/demo/jobs', rootPath: '/tmp/demo', repositoryPath: '/tmp/demo' },
        settings: {
          autoCommit: false, crashRecoveryEnabled: false, autoPushStrategy: 'never',
          runnerMode: null, orchestratorModel: null, laneSortStrategies: {},
        },
        runnerStatus: null, orchestratorLogTail: [], orchestratorSession: null,
        reviewDecisionsPending: [], runnerPendingDecisions: [], publishTargets: [],
        queueHealth: {
          severity: 'ok', issueCount: 0, missingJobJson: [], duplicates: [], stateMismatches: [],
        },
      } });
    }
    if (endpoint === '/api/projects/Demo/style-guides') {
      return route.fulfill({ json: {
        projectKey: 'PROJ-001', projectDisplayName: 'Demo', technologies: [], guides: [],
        warnings: [], snapshotId: 'wiki-source-branch-e2e',
        capturedAtUtc: '2026-07-12T03:00:00Z', refreshAfterUtc: '2026-07-12T04:00:00Z',
      } });
    }
    if (endpoint === '/api/projects/PROJ-001/url-suggestions') return route.fulfill({ json: [] });
    if (endpoint === '/api/cli/maintenance-model') {
      return route.fulfill({ json: { cliType: 'claude', model: null, thinkingLevel: null } });
    }
    if (endpoint.endsWith('/wiki/grading/status')) return route.fulfill({ json: { status: null } });
    return route.fulfill({ json: {} });
  });
});

test('branch source is visible and read-only in both themes', async ({ page }) => {
  await page.goto('/#/projects/demo/wiki', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('project-wiki-section')).toBeVisible({ timeout: 10_000 });
  const indicator = page.getByTestId('project-wiki-source');
  await expect(indicator).toContainText('origin/develop @ abcdef12');
  await expect(indicator).toHaveClass(/source--readonly/);
  await expect(page.getByTestId('project-wiki-new-page')).toBeDisabled();
  await expect(page.getByTestId('project-wiki-new-folder')).toBeDisabled();
  await expect(page.getByText('Unexpected application error')).toHaveCount(0);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
    await page.mouse.move(0, 0);
    const surfaceBackground = await page.getByTestId('project-wiki-viewer-empty')
      .evaluate(element => getComputedStyle(element).backgroundColor);
    const [surfaceRed, surfaceGreen, surfaceBlue] = parseRgb(surfaceBackground);
    if (theme === 'dark') {
      expect(
        Math.max(surfaceRed, surfaceGreen, surfaceBlue),
        'dark Wiki content surface must not fall back to a light paper colour',
      ).toBeLessThan(80);
    } else {
      expect(
        Math.min(surfaceRed, surfaceGreen, surfaceBlue),
        'light Wiki content surface must remain a light paper colour',
      ).toBeGreaterThan(230);
    }
    const tokenColours = await indicator.evaluate((element) => {
      const actual = getComputedStyle(element);
      const colours = {
        actual: {
          background: actual.backgroundColor,
          border: actual.borderTopColor,
          foreground: actual.color,
        },
        expected: { background: '', border: '', foreground: '' },
      };

      const originalStyle = element.getAttribute('style');
      element.style.background = 'var(--studio-commit-bg)';
      element.style.borderColor = 'var(--studio-commit-border)';
      element.style.color = 'var(--studio-commit-fg)';
      const expected = getComputedStyle(element);
      colours.expected = {
        background: expected.backgroundColor,
        border: expected.borderTopColor,
        foreground: expected.color,
      };
      if (originalStyle === null) {
        element.removeAttribute('style');
      } else {
        element.setAttribute('style', originalStyle);
      }
      return colours;
    });
    expect(tokenColours.actual, `${theme} Wiki source Studio token mapping`)
      .toEqual(tokenColours.expected);

    const colours = await sampleColours(page, '[data-testid="project-wiki-source"]');
    expect(
      contrastRatio(colours.color, colours.bg),
      `${theme} Wiki source indicator contrast`,
    ).toBeGreaterThanOrEqual(4.5);
    await page.getByTestId('project-wiki-header').screenshot({
      path: path.join(RESULTS_DIR, `wiki-branch-source--${theme}--real-app.png`),
    });
    const sourceColours = await sampleColours(page, '[data-testid="project-wiki-source"]');
    expect(contrastRatio(sourceColours.color, sourceColours.bg), `${theme} source badge contrast`)
      .toBeGreaterThanOrEqual(4.5);
    const disabledCreateColours = await sampleColours(page, '[data-testid="project-wiki-new-page"]');
    expect(contrastRatio(disabledCreateColours.color, disabledCreateColours.bg), `${theme} read-only action contrast`)
      .toBeGreaterThanOrEqual(4.5);
    await expect(page.getByTestId('project-wiki-new-page')).toHaveCSS('cursor', 'not-allowed');
    if (theme === 'dark') {
      const [red, , blue] = parseRgb(sourceColours.color);
      expect(blue, 'dark read-only source text should use the cool info hierarchy').toBeGreaterThan(red);
    }
    const source = page.getByTestId('project-wiki-source');
    await page.mouse.move(0, 0);
    const borderBeforeHover = await source.evaluate(element => getComputedStyle(element).borderColor);
    await source.hover();
    await expect.poll(() => source.evaluate(element => getComputedStyle(element).borderColor))
      .not.toBe(borderBeforeHover);
  }
});

test('project setting is legible in enabled and disabled states in both themes', async ({ page }) => {
  await page.goto('/#/projects/demo/settings', { waitUntil: 'domcontentloaded' });
  await dismissDevErrorDialog(page);
  const select = page.getByTestId('project-settings-wiki-source-select');
  await expect(select).toBeVisible({ timeout: 10_000 });
  await expect(select).toHaveValue('origin/develop');
  const enabledStyles: Record<'light' | 'dark', { color: string; background: string; border: string }> = {
    light: { color: '', background: '', border: '' },
    dark: { color: '', background: '', border: '' },
  };
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    const cardColours = await sampleColours(page, '[data-testid="project-settings-wiki-source"]');
    expect(contrastRatio(cardColours.color, cardColours.bg), `${theme} Wiki source card contrast`)
      .toBeGreaterThanOrEqual(4.5);
    const descriptionColours = await sampleColours(page, '[data-testid="project-settings-wiki-source-description"]');
    expect(contrastRatio(descriptionColours.color, descriptionColours.bg), `${theme} Wiki source description contrast`)
      .toBeGreaterThanOrEqual(4.5);
    const labelColours = await sampleColours(page, '[data-testid="project-settings-wiki-source-label"]');
    expect(contrastRatio(labelColours.color, labelColours.bg), `${theme} Wiki source label contrast`)
      .toBeGreaterThanOrEqual(4.5);
    const selectColours = await sampleColours(page, '[data-testid="project-settings-wiki-source-select"]');
    expect(contrastRatio(selectColours.color, selectColours.bg), `${theme} Wiki source select contrast`)
      .toBeGreaterThanOrEqual(4.5);
    enabledStyles[theme] = await select.evaluate(element => {
      const style = getComputedStyle(element);
      return { color: style.color, background: style.backgroundColor, border: style.borderColor };
    });
    await page.mouse.move(0, 0);
    const borderBeforeHover = await select.evaluate(element => getComputedStyle(element).borderColor);
    await select.hover();
    await expect.poll(() => select.evaluate(element => getComputedStyle(element).borderColor))
      .not.toBe(borderBeforeHover);
    await select.focus();
    await expect(select).toHaveCSS('outline-style', 'solid');
    await page.getByTestId('project-settings-wiki-source').click({ position: { x: 4, y: 4 } });
    await page.getByTestId('project-settings-wiki-source').screenshot({
      path: path.join(RESULTS_DIR, `wiki-source-setting--${theme}--real-app.png`),
    });
  }
  await select.selectOption('');
  await expect.poll(() => settingsUpdate).toEqual({ clearWikiSourceBranch: true });
  await expect(select).toBeDisabled();
  await expect(select).toHaveCSS('cursor', 'not-allowed');
  await expect(select).toHaveCSS('opacity', '1');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    const disabledColours = await sampleColours(page, '[data-testid="project-settings-wiki-source-select"]');
    expect(contrastRatio(disabledColours.color, disabledColours.bg), `${theme} disabled Wiki source select contrast`)
      .toBeGreaterThanOrEqual(4.5);
    const disabledStyles = await select.evaluate(element => {
      const style = getComputedStyle(element);
      return { color: style.color, background: style.backgroundColor, border: style.borderColor };
    });
    expect(disabledStyles.color, `${theme} disabled foreground`).not.toBe(enabledStyles[theme].color);
    expect(disabledStyles.background, `${theme} disabled background`).not.toBe(enabledStyles[theme].background);
    expect(disabledStyles.border, `${theme} disabled border`).not.toBe(enabledStyles[theme].border);
    await page.getByTestId('project-settings-wiki-source').screenshot({
      path: path.join(RESULTS_DIR, `wiki-source-setting--${theme}--disabled--real-app.png`),
    });
  }
  releaseSettingsUpdate?.();
  await expect(select).toHaveValue('');
});
