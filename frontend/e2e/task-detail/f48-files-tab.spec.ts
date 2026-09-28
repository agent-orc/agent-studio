import { test, expect } from '../fixtures/dev-backend';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

/**
 * Mirror a single page screenshot into the job-folder `results/` directory
 * when the orchestrator passes `F48_RESULTS_DIR`. The same bytes go into
 * the Playwright report via `testInfo.attach`, but writing to disk keeps
 * the F48 acceptance-criteria screenshots co-located with the task.
 */
async function captureScreenshot(
  page: import('@playwright/test').Page,
  testInfo: import('@playwright/test').TestInfo,
  fileName: string
): Promise<void> {
  const buf = await page.screenshot({ fullPage: false });
  await testInfo.attach(fileName, { body: buf, contentType: 'image/png' });
  const dir = process.env.F48_RESULTS_DIR ?? process.env.JOB_RESULTS_DIR;
  if (dir) {
    try {
      await mkdir(dir, { recursive: true });
      await writeFile(join(dir, fileName), buf);
    } catch { /* best-effort */ }
  }
}

/**
 * F48 / AGT-2139 - "Docs" tab on the task-detail prompt pane. The pane used to render
 * only prompt.md under a "Description" label; the F48 redesign surfaces
 * every `.md` in the job folder (prompt + aspect-* + *_NOTE) and labels the
 * tab "Docs". The legacy testid (`prompt-tab-description`) is preserved
 * for backward-compat with older specs.
 */

interface WatchPath { path: string; name?: string }

test.beforeEach(async ({ page, devBackend }) => {
  // The fixture is the repository-owned lifecycle boundary for port 5030.
  // Referencing it here makes this spec self-contained without a persistent
  // dev backend or ad-hoc process control.
  void devBackend;
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      profile: 'networked', bootstrapRequired: false, authenticated: true,
      user: { username: 'playwright', role: 'operator' },
    }),
  }));
});

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function deleteJob(id: string, watchPath: string): Promise<void> {
  try {
    await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'DELETE'
    });
  } catch { /* best-effort cleanup */ }
}

async function setTheme(page: import('@playwright/test').Page, theme: 'light' | 'dark') {
  await page.evaluate((t) => {
    document.documentElement.setAttribute('data-studio-theme', t);
    try { window.localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
}

/**
 * If the auto-update-service banner ("Update failed: verification failed …")
 * is up — which happens whenever a dev backend's own /api/tasks/grouped
 * verification check ran slow on a previous boot — dismiss it so the rest
 * of the detail view is interactable. The banner is harmless to F48 but it
 * paints over the corner of the layout and would skew screenshots.
 */
async function dismissBlockingOverlays(page: import('@playwright/test').Page): Promise<void> {
  // A cold fixture backend can discover existing uncommitted work and open
  // the global crash-recovery prompt. Leave it untouched and close the prompt
  // so this Docs-focused spec can interact with the detail view.
  const leaveAll = page.getByTestId('crash-recovery-dismiss-all');
  await leaveAll.waitFor({ state: 'visible', timeout: 1_500 }).catch(() => undefined);
  if (await leaveAll.isVisible().catch(() => false)) {
    await leaveAll.click({ force: true });
    await expect(page.getByTestId('crash-recovery-prompt-overlay')).toBeHidden();
  }

  const dismiss = page.getByRole('button', { name: /^Dismiss$/ });
  if (await dismiss.count()) {
    try { await dismiss.first().click({ timeout: 1_500 }); } catch { /* best-effort */ }
  }
}

test.describe('Task detail Docs tab - rename + only-prompt + hint', () => {
  test('tab is labeled "Docs" with the legacy data-testid preserved', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f48-rename-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Only prompt\n\nSingle file here.',
      targetState: '2-ready'
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await dismissBlockingOverlays(page);

      const tab = page.getByTestId('prompt-tab-description');
      await expect(tab).toBeVisible({ timeout: 10_000 });
      await expect(tab).toContainText(/Docs/i);
      // The legacy "Description" wording must not leak — the rename is real.
      await expect(tab).not.toContainText(/Description/);

      // No badge when only prompt is present.
      await expect(page.getByTestId('prompt-tab-description-badge')).toHaveCount(0);

      // Overview is the default tab on task switch; click into Docs so
      // the prompt-card / hint card mount.
      await tab.click();

      // Files-pane shell rendered with no expansion before user action.
      const promptCard = page.getByTestId('file-card-prompt.md');
      await expect(promptCard).toBeVisible();
      await expect(promptCard).toHaveAttribute('class', /file-card--collapsed/);

      // Hint card surfaces so the user knows other .md files would appear here.
      const hint = page.getByTestId('files-pane-hint');
      await expect(hint).toBeVisible();
      await expect(hint).toContainText(/agent reports.*will appear in docs/i);
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });
});

test.describe('Task detail Docs tab - multi-document display', () => {
  test('HTML artifact runs scripts while Studio parent access stays blocked', async ({ page }, testInfo) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f48-html-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Interactive report test',
      targetState: '2-ready'
    });

    const html = `<!doctype html><html><body>
      <button id="switch">Switch alternative</button>
      <output id="status">waiting</output>
      <script>
        document.body.dataset.scriptRan = 'true';
        document.querySelector('#switch').addEventListener('click', () => {
          document.querySelector('#status').textContent = 'alternative active';
        });
        try {
          void window.parent.document.body;
          document.body.dataset.parentAccess = 'allowed';
        } catch {
          document.body.dataset.parentAccess = 'blocked';
        }
      </script>
    </body></html>`;

    try {
      await page.route(`**/api/tasks/${job.id}/artifacts**`, route => route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          jobId: job.id,
          files: [
            { name: 'prompt.md', sizeBytes: 25, mtime: '2026-07-11T08:00:00Z', kind: 'prompt' },
            { name: 'interactive-report.html', sizeBytes: html.length, mtime: '2026-07-11T08:01:00Z', kind: 'other' },
          ],
        }),
      }));
      await page.route(`**/api/tasks/${job.id}/files/interactive-report.html**`, route => route.fulfill({
        status: 200,
        contentType: 'text/html; charset=utf-8',
        body: html,
      }));

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await dismissBlockingOverlays(page);
      await page.getByTestId('prompt-tab-description').click();

      const card = page.getByTestId('file-card-interactive-report.html');
      await expect(card).toBeVisible({ timeout: 10_000 });
      await expect(card.getByTestId('file-card-html-isolation-chip')).toHaveText('interactive, isolated');
      await card.getByTestId('file-card-expand-interactive-report.html').click();

      const frame = card.getByTestId('file-card-html-frame');
      await expect(frame).toBeVisible();
      await expect(frame).toHaveAttribute('sandbox', 'allow-scripts');
      const preview = card.frameLocator('[data-testid="file-card-html-frame"]');
      await expect(preview.locator('body')).toHaveAttribute('data-script-ran', 'true');
      await expect(preview.locator('body')).toHaveAttribute('data-parent-access', 'blocked');
      await preview.locator('#switch').click();
      await expect(preview.locator('#status')).toHaveText('alternative active');

      await captureScreenshot(page, testInfo, 'files-tab-html-interactive-isolated.png');
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });

  test('outcome documents lead, open rendered, and expose metadata only on demand', async ({ page }, testInfo) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f48-multi-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown:
        '# Multi-file task\n\nFirst paragraph of the prompt body.\n\n' +
        'Second paragraph so the preview has something to truncate.\n',
      targetState: '2-ready'
    });

    try {
      const artifactBodies: Record<string, string> = {
        'aspect-requirement-fit.md':
          '# requirement-fit\n\n- Does the change deliver F48?\n- Yes: prompt + aspects show.\n',
        'aspect-code-quality.md':
          '# code-quality\n\nNo new lint warnings; new component stays within size budgets.\n',
        'aspect-tests-and-evidence.md':
          '# tests\n\nUnit-test the sort + classification; Playwright covers the UI.\n',
        'REVIEW_NOTE.md':
          '# Review note\n\nLook at the focus-visible state on the file-card head.\n',
        'code-review-grade-2026-07-11.md':
          '---\nverdict: concerns\ngrade: C\nmodel: gpt-5\n---\n\n# Code review\n\nTwo details deserve attention.\n',
      };
      const statusMarkdown =
        '# Status\n\n- Result: Success\n- Case: Bugfix\n\n## Overview\n- Problem: Dense operator result.\n- Solution: Navigable document view.\n';
      await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${job.id}(?:\\?.*)?$`), async route => {
        const response = await route.fetch();
        const detail = await response.json();
        detail.info.tags = [...(detail.info.tags ?? []), 'code-review:grade-c'];
        detail.statusMarkdown = statusMarkdown;
        detail.statusGeneration = {
          file: 'status.md', kind: 'summary', model: 'gpt-5-mini', cli: 'codex',
          tokensIn: 900, tokensOut: 180, tokensTotal: 1080, durationMs: 2200,
        };
        await route.fulfill({ response, json: detail });
      });
      await page.route(new RegExp(`/api/tasks/${job.id}/artifacts(?:\\?.*)?$`), route => {
        const files = [
          { name: 'prompt.md', sizeBytes: 122, mtime: '2026-07-11T08:00:00Z', kind: 'prompt' },
          { name: 'aspect-requirement-fit.md', sizeBytes: artifactBodies['aspect-requirement-fit.md'].length, mtime: '2026-07-11T08:01:00Z', kind: 'aspect', aspectName: 'requirement-fit' },
          { name: 'aspect-code-quality.md', sizeBytes: artifactBodies['aspect-code-quality.md'].length, mtime: '2026-07-11T08:02:00Z', kind: 'aspect', aspectName: 'code-quality' },
          { name: 'aspect-tests-and-evidence.md', sizeBytes: artifactBodies['aspect-tests-and-evidence.md'].length, mtime: '2026-07-11T08:03:00Z', kind: 'aspect', aspectName: 'tests-and-evidence' },
          { name: 'REVIEW_NOTE.md', sizeBytes: artifactBodies['REVIEW_NOTE.md'].length, mtime: '2026-07-11T08:04:00Z', kind: 'note' },
          {
            name: 'code-review-grade-2026-07-11.md',
            sizeBytes: artifactBodies['code-review-grade-2026-07-11.md'].length,
            mtime: '2026-07-11T08:05:00Z',
            kind: 'codeReview',
            generation: {
              file: 'code-review-grade-2026-07-11.md', kind: 'code-review', model: 'gpt-5', cli: 'codex',
              tokensIn: 1000, tokensOut: 250, tokensTotal: 1250, durationMs: 3100,
            },
          },
        ];
        return route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ jobId: job.id, files }),
        });
      });
      await page.route(new RegExp(`/api/tasks/${job.id}/files/[^?]+(?:\\?.*)?$`), route => {
        const marker = `/api/tasks/${job.id}/files/`;
        const path = new URL(route.request().url()).pathname;
        const name = decodeURIComponent(path.slice(path.indexOf(marker) + marker.length));
        const body = artifactBodies[name];
        if (body === undefined) return route.fallback();
        return route.fulfill({ status: 200, contentType: 'text/markdown; charset=utf-8', body });
      });

      await page.setViewportSize({ width: 1600, height: 1000 });
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await dismissBlockingOverlays(page);

      // Tab badge surfaces the file count once we cross 1.
      const badge = page.getByTestId('prompt-tab-description-badge');
      await expect(badge).toBeVisible({ timeout: 10_000 });
      await expect(badge).toHaveText('6');

      // The Grade chip in Result is navigation, not decoration.
      const grade = page.getByTestId('result-metric-grade');
      await expect(grade).toBeVisible({ timeout: 10_000 });
      await grade.click();
      await expect(page.getByTestId('prompt-tab-description')).toHaveAttribute('aria-selected', 'true');
      await expect(page.getByTestId('file-card-code-review-grade-2026-07-11.md')).toBeFocused();

      // Hint card must disappear when more than one file is present.
      await expect(page.getByTestId('files-pane-hint')).toHaveCount(0);

      // Read order: result documents first, source prompt afterwards.
      const expectedOrder = [
        'file-card-code-review-grade-2026-07-11.md',
        'file-card-aspect-code-quality.md',
        'file-card-aspect-requirement-fit.md',
        'file-card-aspect-tests-and-evidence.md',
        'file-card-REVIEW_NOTE.md',
        'file-card-prompt.md',
      ];
      // Articles only — the `file-card-prompt-edit`/`-cancel` buttons and the
      // `file-card-expand-<name>` expand-links inherit the same prefix.
      const cards = page.locator('article[data-testid^="file-card-"]');
      await expect(cards).toHaveCount(expectedOrder.length);
      const seen = await cards.evaluateAll((nodes) =>
        nodes.map((n) => (n as HTMLElement).getAttribute('data-testid'))
      );
      expect(seen).toEqual(expectedOrder);

      // Outcome documents are already readable; the source prompt stays compact.
      for (const id of expectedOrder.slice(0, 5)) {
        await expect(page.getByTestId(id)).toHaveAttribute('class', /file-card--expanded/);
      }
      for (const id of expectedOrder.slice(5)) {
        await expect(page.getByTestId(id)).toHaveAttribute('class', /file-card--collapsed/);
      }

      const review = page.getByTestId('file-card-code-review-grade-2026-07-11.md');
      await expect(review.getByTestId('file-card-topic')).toHaveText('Code review');
      await expect(review.getByTestId('file-card-verdict')).toHaveText('Concerns');
      await expect(review.getByTestId('file-card-model')).toHaveText('gpt-5');
      await expect(review).not.toContainText('verdict: concerns');
      await expect(review.getByTestId('file-source-history')).toHaveCount(0);
      await review.getByTestId('file-card-details-code-review-grade-2026-07-11.md').click();
      const details = review.getByTestId('file-card-details-menu');
      await expect(details).toContainText('code-review-grade-2026-07-11.md');
      await expect(details).toContainText('1,250 total');
      await expect(details.getByTestId('file-card-history-code-review-grade-2026-07-11.md')).toBeVisible();
      const resultCase = page.getByTestId('result-case-badge');
      const provenance = page.getByTestId('protocol-provenance');
      await expect(resultCase).toContainText('Bugfix');
      await expect(provenance).toContainText(/Generated by.*codex \/ gpt-5-mini/);
      const [caseBox, provenanceBox] = await Promise.all([resultCase.boundingBox(), provenance.boundingBox()]);
      expect(caseBox).not.toBeNull();
      expect(provenanceBox).not.toBeNull();
      expect(Math.abs(caseBox!.y - provenanceBox!.y)).toBeLessThan(8);

      // Light-theme screenshot of the outcome-first document surface.
      await setTheme(page, 'light');
      await captureScreenshot(page, testInfo, 'operator-docs-outcome-first-light.png');
      await review.getByTestId('file-card-details-code-review-grade-2026-07-11.md').click();

      // Click an aspect card -> it expands and renders markdown (h1 visible).
      const aspect = page.getByTestId('file-card-aspect-code-quality.md');
      const aspectHeader = aspect.getByRole('button', { name: /toggle document code-quality/i });
      await aspectHeader.click();
      await expect(aspect).toHaveAttribute('class', /file-card--collapsed/);

      // Dark-theme screenshot preserves the same hierarchy and contrast.
      await aspectHeader.click();
      await setTheme(page, 'dark');
      await captureScreenshot(page, testInfo, 'operator-docs-outcome-first-dark.png');
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });
});

test.describe('Task detail Docs tab - only-prompt theme screenshots + Edit flow', () => {
  for (const theme of ['light', 'dark'] as const) {
    test(`only-prompt looks right in the ${theme} theme`, async ({ page }, testInfo) => {
      const watchPath = await pickWatchPath();
      const job = await createJob({
        title: `f48-only-prompt-${theme}-${Date.now()}`,
        watchPath,
        cliType: 'claude',
        agent: 'claude',
        promptMarkdown:
          '# Just the prompt\n\nThis task has only `prompt.md` in its folder. ' +
          'The hint below should explain that more files would render automatically.\n',
        targetState: '2-ready'
      });

      try {
        await page.setViewportSize({ width: 1400, height: 900 });
        await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
        await dismissBlockingOverlays(page);
        await setTheme(page, theme);

        // Click the label itself because the trailing edge sits below the pane
        // action cluster at this compact screenshot viewport.
        await page.getByTestId('prompt-tab-description').getByText('Docs', { exact: true }).click();

        await expect(page.getByTestId('file-card-prompt.md')).toBeVisible({ timeout: 10_000 });
        await expect(page.getByTestId('files-pane-hint')).toBeVisible();

        await captureScreenshot(page, testInfo, `f48-files-tab-only-prompt-${theme}.png`);
      } finally {
        await deleteJob(job.id, watchPath);
      }
    });
  }

  test('Edit button on the prompt card opens the rich editor and Done returns to the rendered view', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f48-edit-prompt-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Edit me\n\nClick Edit and you should see the rich editor.',
      targetState: '2-ready'
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await dismissBlockingOverlays(page);

      // Initially the editor must not be rendered — read-only markdown only.
      await expect(page.getByTestId('prompt-editor')).toHaveCount(0);

      // Overview is the default tab on task switch; click into Docs, then
      // explicitly expand prompt.md before editing it.
      await page.getByTestId('prompt-tab-description').click();
      const promptCard = page.getByTestId('file-card-prompt.md');
      await promptCard.getByRole('button', { name: /toggle document edit me/i }).click();

      const edit = page.getByTestId('file-card-prompt-edit');
      await expect(edit).toBeVisible({ timeout: 10_000 });
      await edit.click();

      const editor = page.getByTestId('prompt-editor');
      await expect(editor).toBeVisible({ timeout: 5_000 });

      // Done switches back to rendered markdown.
      await page.getByTestId('file-card-prompt-cancel').click();
      await expect(page.getByTestId('prompt-editor')).toHaveCount(0);
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });
});
