import { test, expect, Page } from '@playwright/test';

/**
 * GitView polish — contrast + collapsible commit-message banner.
 *
 * 1. Datei-tree text in the commit-mode split layout must use semantic
 *    `--studio-fg*` / `--studio-accent*` tokens (not hardcoded slate hex
 *    values from the dark theme) so it stays WCAG-AA legible in BOTH
 *    light + dark shells. Previously the tree used #cbd5e1 / #94a3b8 /
 *    #a5b4fc as literal hex values, which rendered as washed-out light
 *    grey on the light theme's near-white surface.
 *
 * 2. The commit-message banner above the tree+diff split must be
 *    collapsible via a dedicated caret toggle. The collapsed preference
 *    persists in localStorage so the operator's choice survives a
 *    reload, and re-expanding restores the message body.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/gitview-polish';
const JOB_ID = 'gitview-polish-test';

const COMMIT = {
  sha: 'feedbeef1234567890feedbeef1234567890feed',
  shortSha: 'feedbee',
  message:
    'crash-recovery: orphan changes for human-decision-needed-bug-collapsed-lane-identity-lost-and-cascade\n\nLonger body that should be hidden when the banner is collapsed.',
  filesChanged: 2,
  files: ['frontend/e2e/board/collapsed-lane-identity-and-cascade.spec.ts', 'frontend/e2e/board/collapsed-lane-independent.spec.ts'],
  at: '2026-05-28T08:00:00Z',
};

function makeDetail(): unknown {
  return {
    info: {
      id: JOB_ID,
      jobKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Polished git view fixture',
      state: '5-human-review',
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/5-human-review/${JOB_ID}`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: COMMIT,
      commits: [COMMIT],
      ownerClientId: 'local-default',
    },
    promptMarkdown: 'Polished git view test prompt.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installRoutes(page: Page): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail();

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => { /* ignore late fulfill */ });
  });
  await page.route(/\/api\/(?:jobs|tasks)(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(/\/api\/v1\/studio\/board/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        preparation: [], orchestratorPrep: [], ready: [],
        progress: [], failedPickup: [], autoReview: [], humanReview: [], completed: [], archive: [],
      }),
    }),
  );
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }),
  );
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/(?:projects|v1\/projects(?:\?|$))/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }),
  );
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
        },
      }),
    }),
  );
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/claude-session(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/git/hygiene(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projectName: PROJECT, isRepo: true, isDirty: false, hasUpstream: true, ahead: 0, behind: 0,
        job: { jobId: JOB_ID, state: '5-human-review', jobInfoCommitPresent: true, stampedCommitSha: COMMIT.sha, acceptedTaskUncommitted: false },
        error: null,
      }),
    }),
  );
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/git/status(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isRepo: true, branch: 'main', filesChanged: 0, totalAdded: 0, totalRemoved: 0, files: [], error: null }),
    }),
  );
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/git/diff\\?.*`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'text/plain',
      body: `diff --git a/${COMMIT.files[0]} b/${COMMIT.files[0]}\n--- a/${COMMIT.files[0]}\n+++ b/${COMMIT.files[0]}\n@@ -1,3 +1,4 @@\n context\n+added line\n-removed line\n`,
    }),
  );
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/(?:commit|commits/[^/]+)/diff\\b`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'text/plain',
      body: `diff --git a/${COMMIT.files[0]} b/${COMMIT.files[0]}\n--- a/${COMMIT.files[0]}\n+++ b/${COMMIT.files[0]}\n@@ -1,3 +1,4 @@\n context\n+added line\n-removed line\n`,
    }),
  );
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/commits/[^/]+/files`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ sha: COMMIT.sha, files: COMMIT.files.map((p) => ({ status: 'M', path: p, added: 4, removed: 1 })) }),
    }),
  );
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/commit(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        commit: COMMIT,
        files: COMMIT.files.map((p) => ({ status: 'M', path: p, added: 4, removed: 1 })),
      }),
    }),
  );
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }),
  );
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
  await page.waitForTimeout(80);
}

async function dismissErrorDialog(page: Page): Promise<void> {
  const overlay = page.getByTestId('error-dialog-overlay');
  if (await overlay.isVisible().catch(() => false)) {
    await page.evaluate(() => {
      const el = document.querySelector<HTMLElement>('[data-testid="error-dialog-overlay"]');
      el?.click();
    });
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => { /* ignore */ });
  }
}

function parseRgb(value: string): [number, number, number, number] {
  const m = /rgba?\(\s*([\d.]+)[ ,]+([\d.]+)[ ,]+([\d.]+)(?:[ ,/]+([\d.]+))?\s*\)/.exec(value);
  if (!m) throw new Error(`Cannot parse colour: ${value}`);
  return [Number(m[1]), Number(m[2]), Number(m[3]), m[4] === undefined ? 1 : Number(m[4])];
}

function luminance(rgb: [number, number, number]): number {
  const [r, g, b] = rgb.map(c => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrastRatioOpaque(fg: [number, number, number], bg: [number, number, number]): number {
  const l1 = luminance(fg);
  const l2 = luminance(bg);
  const [light, dark] = l1 > l2 ? [l1, l2] : [l2, l1];
  return (light + 0.05) / (dark + 0.05);
}

function composite(colour: [number, number, number, number], surface: [number, number, number]): [number, number, number] {
  const a = colour[3];
  return [
    Math.round(colour[0] * a + surface[0] * (1 - a)),
    Math.round(colour[1] * a + surface[1] * (1 - a)),
    Math.round(colour[2] * a + surface[2] * (1 - a)),
  ];
}

function contrastOnSurface(fgRaw: string, bgRaw: string, surfaceRaw: string): number {
  const fg = parseRgb(fgRaw);
  const bg = parseRgb(bgRaw);
  const surface = parseRgb(surfaceRaw);
  const surfaceRgb: [number, number, number] = [surface[0], surface[1], surface[2]];
  const effectiveBg = composite(bg, surfaceRgb);
  const effectiveFg = composite(fg, effectiveBg);
  return contrastRatioOpaque(effectiveFg, effectiveBg);
}

test.describe('GitView polish — contrast + collapsible commit-message banner', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        // Open the git pane by default so the spec doesn't have to click
        // through the toggle each run; matches the operator's reported flow.
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: false, protocol: true, git: true }),
        );
        localStorage.setItem('taskboard.activeInspectorTab', '"activity"');
      } catch { /* private mode */ }
    });
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`file-tree + commit-header text meets WCAG-AA (≥ 4.5:1) in ${theme} theme`, async ({ page }) => {
      await installRoutes(page);
      await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
      await setTheme(page, theme);

      const treeRow = page.locator('[data-testid="git-tree-file"]').first();
      await expect(treeRow).toBeVisible({ timeout: 10_000 });

      const probes = await page.evaluate(() => {
        // getComputedStyle may return `color(srgb ...)` for color-mix()
        // results; normalize every value through a 1x1 canvas which
        // forces it to rgba() form.
        const canvas = document.createElement('canvas');
        canvas.width = canvas.height = 1;
        const ctx = canvas.getContext('2d')!;
        function normalize(value: string): string {
          ctx.clearRect(0, 0, 1, 1);
          ctx.fillStyle = '#000';
          ctx.fillStyle = value;
          ctx.fillRect(0, 0, 1, 1);
          const d = ctx.getImageData(0, 0, 1, 1).data;
          return `rgba(${d[0]}, ${d[1]}, ${d[2]}, ${(d[3] / 255).toFixed(3)})`;
        }
        function fgBg(el: HTMLElement | null) {
          if (!el) return null;
          const cs = getComputedStyle(el);
          return { color: normalize(cs.color), bg: normalize(cs.backgroundColor) };
        }
        function surfaceOf(el: HTMLElement | null): string {
          let cur: HTMLElement | null = el;
          while (cur) {
            const cs = getComputedStyle(cur);
            const norm = normalize(cs.backgroundColor);
            const m = /rgba?\(\s*([\d.]+)[ ,]+([\d.]+)[ ,]+([\d.]+)(?:[ ,/]+([\d.]+))?\s*\)/.exec(norm);
            if (m) {
              const alpha = m[4] === undefined ? 1 : Number(m[4]);
              if (alpha > 0.95) return norm;
            }
            cur = cur.parentElement;
          }
          return normalize(getComputedStyle(document.body).backgroundColor);
        }
        const fileRow = document.querySelector<HTMLElement>('[data-testid="git-tree-file"]');
        const fileLabel = fileRow?.querySelector<HTMLElement>('.git-tree__label') ?? null;
        const folderLabel = document.querySelector<HTMLElement>('[data-testid="git-tree-folder"] .git-tree__label--folder');
        const commitHeader = document.querySelector<HTMLElement>('[data-testid="git-commit-header"]');
        const commitMsg = document.querySelector<HTMLElement>('[data-testid="git-commit-message"]');
        const surface = surfaceOf(fileRow);
        return {
          fileLabel: fgBg(fileLabel),
          folderLabel: folderLabel ? fgBg(folderLabel) : null,
          commitHeader: fgBg(commitHeader),
          commitMsg: fgBg(commitMsg),
          surface,
        };
      });

      const surface = probes.surface;
      const pairs: [string, { color: string; bg: string } | null][] = [
        ['fileLabel', probes.fileLabel],
        ['folderLabel', probes.folderLabel],
        ['commitHeader', probes.commitHeader],
        ['commitMsg', probes.commitMsg],
      ];

      for (const [name, p] of pairs) {
        if (!p) continue;
        const ratio = contrastOnSurface(p.color, p.bg, surface);
        expect(
          ratio,
          `[${theme}] ${name}: contrast ${ratio.toFixed(2)} (${p.color} on ${p.bg} over ${surface}) must be ≥ 4.5`
        ).toBeGreaterThanOrEqual(4.5);
      }
    });
  }

  test('commit-message banner is collapsible, persists, and re-expands', async ({ page }) => {
    await installRoutes(page);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

    const header = page.getByTestId('git-commit-header');
    const messageBody = page.getByTestId('git-commit-message');
    const toggle = page.getByTestId('git-commit-collapse-toggle');

    await expect(header).toBeVisible({ timeout: 10_000 });
    await expect(messageBody).toBeVisible();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');

    await dismissErrorDialog(page);
    await page.evaluate(() => {
      document.querySelector<HTMLElement>('[data-testid="git-commit-collapse-toggle"]')?.click();
    });
    await expect(messageBody).toHaveCount(0);
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');

    // Collapsed banner still shows the subject line as a one-row summary.
    await expect(page.getByTestId('git-commit-subject')).toBeVisible();

    // Persistence: reload and confirm the collapse survives.
    const stored = await page.evaluate(() => localStorage.getItem('taskboard.gitPane.commitHeaderCollapsed'));
    expect(stored).toBe('1');

    await page.reload();
    await expect(page.getByTestId('git-commit-header')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('git-commit-message')).toHaveCount(0);
    await expect(page.getByTestId('git-commit-collapse-toggle')).toHaveAttribute('aria-expanded', 'false');

    // Re-expand restores the message body.
    await dismissErrorDialog(page);
    await page.evaluate(() => {
      document.querySelector<HTMLElement>('[data-testid="git-commit-collapse-toggle"]')?.click();
    });
    await expect(page.getByTestId('git-commit-message')).toBeVisible();
    const restored = await page.evaluate(() => localStorage.getItem('taskboard.gitPane.commitHeaderCollapsed'));
    expect(restored).toBe('0');
  });
});
