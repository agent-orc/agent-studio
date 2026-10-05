import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

const SHOTS_DIR = process.env['JOB_RESULTS_DIR']
  ?? path.resolve(__dirname, '../../results/AGT-2437');
const EVIDENCE_VARIANT = process.env['EVIDENCE_VARIANT'] === 'before' ? 'before' : 'after';
const TARGET = {
  id: 'activity-structured-content-fixture',
  watchPath: 'C:/fixtures/activity-structured-content',
};
const WORKTREE_ROOT = 'C:/Users/operator/AppData/Local/Temp/ass-worktrees/fixture/activity-structured-content-fixture';

interface OutLine {
  timestamp: string;
  stream: string;
  text: string;
}

function outputBuffer(): OutLine[] {
  const t0 = Date.parse('2026-07-28T10:40:44.000Z');
  const at = (offset: number) => new Date(t0 + offset * 1000).toISOString();
  return [
    { timestamp: at(-110), stream: 'stdout', text: 'I will inspect the implementation before editing it.' },
    { timestamp: at(-108), stream: 'stdout', text: '* Run npm --prefix frontend run test:one (shell)' },
    { timestamp: at(-106), stream: 'stdout', text: '* Run npm --prefix frontend run test:two (shell)' },
    { timestamp: at(-104), stream: 'stdout', text: '* Run npm --prefix frontend run test:three (shell)' },
    { timestamp: at(-102), stream: 'stdout', text: '* Run npm --prefix frontend run test:four (shell)' },
    { timestamp: at(-100), stream: 'stdout', text: '* Read frontend/src/app/campaign.ts' },
    { timestamp: at(-98), stream: 'stdout', text: '* Read frontend/src/app/campaign.spec.ts' },
    { timestamp: at(-80), stream: 'stdout', text: 'The call sites are mapped. I will apply the focused edits now.' },
    { timestamp: at(-78), stream: 'stdout', text: `* Edit ${WORKTREE_ROOT}\\frontend\\src\\app\\campaign.ts` },
    { timestamp: at(-76), stream: 'stdout', text: `* Edit ${WORKTREE_ROOT}\\frontend\\src\\app\\campaign.ts` },
    { timestamp: at(-74), stream: 'stdout', text: `* Edit ${WORKTREE_ROOT}\\frontend\\src\\app\\campaign.ts` },
    { timestamp: at(-72), stream: 'stdout', text: `* Edit ${WORKTREE_ROOT}\\frontend\\src\\app\\campaign.ts` },
    { timestamp: at(-70), stream: 'stdout', text: `* Edit ${WORKTREE_ROOT}\\frontend\\src\\app\\campaign.ts` },
    { timestamp: at(-60), stream: 'stdout', text: 'The edits are complete. I will verify the structured transcript.' },
    { timestamp: at(0), stream: 'system', text: "[runner] working tree ready on branch 'main'" },
    { timestamp: at(1), stream: 'system', text: '[runner] spawning codex exec -m gpt-5.6-sol -' },
    { timestamp: at(2), stream: 'stderr', text: 'OpenAI Codex v0.144.1' },
    { timestamp: at(3), stream: 'stderr', text: '--------' },
    { timestamp: at(4), stream: 'stderr', text: 'workdir: /workspace/AGT-2355' },
    { timestamp: at(5), stream: 'stderr', text: 'user' },
    { timestamp: at(6), stream: 'stderr', text: 'Create the Deck icon exploration.' },
    { timestamp: at(7), stream: 'stderr', text: 'codex' },
    { timestamp: at(8), stream: 'stderr', text: 'I will inspect the current work and verify the generated files.' },
    { timestamp: at(9), stream: 'system', text: '[runner-log-delivery:fixture-1]' },
    { timestamp: at(10), stream: 'stderr', text: 'exec' },
    {
      timestamp: at(11),
      stream: 'stderr',
      text: '/bin/bash -lc "git diff -- docs/start/README.md docs/operations/deck-icon-exploration/workbench.json"',
    },
    { timestamp: at(12), stream: 'stderr', text: ' succeeded in 18ms:' },
    {
      timestamp: at(13),
      stream: 'stderr',
      text: 'diff --git a/docs/start/README.md b/docs/start/README.md',
    },
    { timestamp: at(14), stream: 'stderr', text: '+{' },
    {
      timestamp: at(15),
      stream: 'stderr',
      text: '+  "title": "Apply Robert\'s selected Deck icon",',
    },
    { timestamp: at(16), stream: 'stderr', text: '+}' },
    { timestamp: at(17), stream: 'system', text: '[runner-log-delivery:fixture-2]' },
    { timestamp: at(18), stream: 'stderr', text: 'codex' },
    {
      timestamp: at(19),
      stream: 'stderr',
      text: 'I will inspect the Wiki concept document before wrapping up.',
    },
    { timestamp: at(20), stream: 'stderr', text: 'read_file' },
    {
      timestamp: at(21),
      stream: 'stderr',
      text: 'docs/concepts/wiki-concept.html',
    },
    { timestamp: at(22), stream: 'stderr', text: ' succeeded in 12ms:' },
    { timestamp: at(23), stream: 'stderr', text: '<!doctype html>' },
    { timestamp: at(24), stream: 'stderr', text: '<html lang="en">' },
    { timestamp: at(25), stream: 'stderr', text: '<head>' },
    { timestamp: at(26), stream: 'stderr', text: '  <meta charset="utf-8">' },
    { timestamp: at(27), stream: 'stderr', text: '  <style>' },
    { timestamp: at(28), stream: 'stderr', text: '    .concept-grid {' },
    { timestamp: at(29), stream: 'stderr', text: '      display: grid;' },
    { timestamp: at(30), stream: 'stderr', text: '      grid-template-columns: 1fr 1fr;' },
    { timestamp: at(31), stream: 'stderr', text: '    }' },
    { timestamp: at(32), stream: 'stderr', text: '  </style>' },
    { timestamp: at(33), stream: 'stderr', text: '</head>' },
    { timestamp: at(34), stream: 'stderr', text: '<body>' },
    {
      timestamp: at(35),
      stream: 'stderr',
      text: '  <p class="lead">The concept remains readable as source.</p>',
    },
    { timestamp: at(36), stream: 'stderr', text: '</body>' },
    { timestamp: at(37), stream: 'stderr', text: '</html>' },
    { timestamp: at(38), stream: 'stderr', text: 'codex' },
    {
      timestamp: at(39),
      stream: 'stderr',
      text: [
        'The concept and its navigation entry are ready for review. Prose card ASS-4242 remains linked.',
        '',
        'diff --git a/docs/rendering-defense.md b/docs/rendering-defense.md',
        'index 1111111..2222222 100644',
        '--- a/docs/rendering-defense.md',
        '+++ b/docs/rendering-defense.md',
        '@@ -1,2 +1,8 @@',
        '-| Old | ASS-4242 |',
        '+| New | ASS-4242 |',
        '+.result-card {',
        '+  display: grid;',
        '+}',
        '+<header class="page">',
        '+  <svg viewBox="0 0 24 24"></svg>',
        '+</header>',
      ].join('\n'),
    },
    {
      timestamp: at(39.5),
      stream: 'stderr',
      text: 'Fertig: [Konzeptbericht öffnen](/home/agent/runner-work/tasks/AGT-2514/results/report.html)',
    },
    {
      timestamp: at(39.5),
      stream: 'stdout',
      text: [
        'The pinned gallery evidence is ready:',
        '- [Dashboard light](results/gallery-dashboard-light.png)',
        '- [Dashboard dark](results/gallery-dashboard-dark.png)',
        '- [Export dialog](results/gallery-export-dialog.png)',
        '- [Empty state](results/gallery-empty-state.png)',
        '- [Filter panel](results/gallery-filter-panel.png)',
        '- [Mobile table](results/gallery-mobile-table.png)',
        '- [Review light](results/gallery-review-light.png)',
        '- [Review dark](results/gallery-review-dark.png)',
        '- [Delivery diff](results/delivery.diff)',
        '- [Gallery notes](results/gallery-notes.md)',
        '- [Gallery metrics](results/gallery-metrics.json)',
        '- [Capture log](results/gallery-run.log)',
      ].join('\n'),
    },
    { timestamp: at(40), stream: 'stderr', text: '[[TASK_DONE]]' },
    {
      timestamp: at(41),
      stream: 'system',
      text: '[runner] CLI exited 0; typedOutcome=ExplicitAgentDone classifier=execution-outcome/v1',
    },
  ];
}

function detail() {
  return {
    info: {
      id: TARGET.id,
      taskKey: 'ASS-4242',
      displayKey: 'ASS-4242',
      title: 'Activity structured content fixture',
      state: '5-human-review',
      order: 1,
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5.6-sol',
      thinkingLevel: 'medium',
      watchPath: TARGET.watchPath,
      projectName: 'fixture',
      folderPath: `${TARGET.watchPath}/tasks/${TARGET.id}`,
      createdAt: '2026-07-28T10:40:00.000Z',
      lastActivity: '2026-07-28T10:41:10.000Z',
      sessionName: null,
      useOwnSession: null,
      lastUsage: null,
      execution: null,
      executionLocation: {
        state: 'no-active-execution',
        executionKind: 'none',
        worktreePath: `${WORKTREE_ROOT}/frontend`,
        connectionState: 'idle',
        leaseState: 'none',
        trustReason: 'Historical fixture worktree.',
      },
      commit: null,
      commits: [],
      codeActivityDetected: true,
      summaryState: null,
      taskType: 'bug',
      tags: [],
      references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    },
    promptMarkdown: [
      'Fixture prompt.',
      '',
      '[Task report](/home/agent/runner-work/tasks/AGT-2514/results/report.html)',
    ].join('\n'),
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: `# Status

- Result: Success

## Overview

- Problem: Result links left the application.
- Solution: Internal destinations now use Studio navigation.

## References

- [Convention](docs/quality/angular-components.md)
- [HTML report](results/report.html)
- [Card](#/tasks/ASS-4242)
- [External](https://example.com)

[[TASK_DONE]]
`,
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/hubs/v1/studio/negotiate**', (route) => route.fulfill({
    json: {
      connectionId: 'activity-gallery-e2e',
      connectionToken: 'activity-gallery-e2e',
      negotiateVersion: 1,
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
    },
  }));
  await page.routeWebSocket('**/hubs/v1/studio**', (socket) => {
    socket.onMessage((message) => {
      if (message.toString().includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  const context = page.context();
  const encodedId = encodeURIComponent(TARGET.id);
  await context.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await context.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      }),
    }));
  await context.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        backlog: [],
        preparation: [],
        orchestratorPrep: [],
        ready: [],
        progress: [],
        failedPickup: [],
        codeNotComplete: [],
        autoReview: [],
        humanReview: [detail().info],
        review: [],
        completed: [],
        archive: [],
      }),
    }));
  await context.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: 'fixture', path: TARGET.watchPath, rootPath: TARGET.watchPath }]),
    }));
  const project = {
    id: 'fixture',
    displayName: 'fixture',
    shortCode: 'ASS',
    workspaceId: 'workspace-fixture',
    storageLocation: TARGET.watchPath,
    rootPath: TARGET.watchPath,
    repositoryPath: TARGET.watchPath,
    sortOrder: 0,
    archived: false,
    urls: [],
  };
  await context.route('**/api/v1/projects', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([project]),
    }));
  await context.route('**/api/v1/workspaces', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 'workspace-fixture',
        displayName: 'Fixture',
        color: '#6c8cff',
        projects: [project],
      }]),
    }));
  await context.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ snapshots: [], ttlSeconds: 600 }),
    }));
  await context.route('**/api/v1/studio/runner/status**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          fixture: {
            projectName: 'fixture',
            mode: 'manual',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    }));
  await context.route('**/api/projects/*/workbenches**', (route) => {
    const requestUrl = new URL(route.request().url());
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projectName: 'fixture',
        includesHistory: requestUrl.searchParams.get('history') === 'true',
        count: 0,
        items: [],
      }),
    });
  });
  await context.route('**/api/workbenches**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projectName: null,
        count: 0,
        currentCount: 0,
        historyCount: 0,
        items: [],
      }),
    }));
  await context.route('**/api/tasks/reference-status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ items: [] }),
    }));
  await context.route(`**/api/tasks/${encodedId}/output**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(outputBuffer()),
    }));
  await context.route(`**/api/tasks/${encodedId}/plan**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        hasPlan: false,
        source: null,
        snapshotCount: 0,
        activeItemId: null,
        softEstimateMedian: null,
        items: [],
        unassignedSubActions: [],
      }),
    }));
  await context.route(`**/api/tasks/${encodedId}/pipeline**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        pipeline: { pre: [], core: [], post: [], allSteps: [] },
        execution: null,
        executions: [],
        config: {},
        cost: null,
      }),
    }));
  await context.route(`**/api/tasks/${encodedId}/runs**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ runs: [], runnerEvents: [] }),
    }));
  await context.route(`**/api/tasks/${encodedId}/session-events**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ events: [], sessionChain: [] }),
    }));
  await context.route(`**/api/tasks/${encodedId}/claude-session**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await context.route(`**/api/v1/projects/*/tasks/${encodedId}?**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(detail()),
    }));
  await context.route(`**/api/tasks/${encodedId}/results/report.html**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'text/html',
      headers: { 'Content-Disposition': 'inline' },
      body: '<!doctype html><html><body><h1>Artifact opened</h1></body></html>',
    }));
  await context.route(`**/api/tasks/${encodedId}/thumbnail?**`, (route) => {
    const fileName = new URL(route.request().url()).searchParams.get('path') ?? 'artifact';
    return route.fulfill({
      status: 200,
      contentType: 'image/svg+xml',
      body: `<svg xmlns="http://www.w3.org/2000/svg" width="720" height="450" viewBox="0 0 720 450"><rect width="720" height="450" fill="#18202d"/><rect x="24" y="24" width="672" height="48" rx="8" fill="#2d3a50"/><rect x="24" y="96" width="170" height="330" rx="8" fill="#243044"/><rect x="218" y="96" width="478" height="330" rx="8" fill="#243044"/><rect x="248" y="130" width="320" height="18" rx="5" fill="#76a8ff"/><text x="248" y="200" fill="#dce8ff" font-family="sans-serif" font-size="24">${fileName}</text></svg>`,
    });
  });
  await context.route(`**/api/tasks/${encodedId}/screenshot?**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'image/svg+xml',
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="1440" height="900"><rect width="1440" height="900" fill="#18202d"/><rect x="64" y="64" width="1312" height="772" rx="24" fill="#2d3a50"/></svg>',
    }));
  await context.route(`**/api/tasks/${encodedId}/files/results/**`, (route) => {
    const url = route.request().url();
    if (url.includes('delivery.diff')) {
      return route.fulfill({ status: 200, contentType: 'text/plain', body: '@@ -1 +1 @@\n-old export\n+typed gallery export' });
    }
    if (url.includes('gallery-notes.md')) {
      return route.fulfill({ status: 200, contentType: 'text/markdown', body: '# Gallery notes\n\nPinned demo evidence.' });
    }
    if (url.includes('gallery-metrics.json')) {
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{"images":8,"pinned":true}' });
    }
    return route.fulfill({ status: 200, contentType: 'text/plain', body: 'capture light: ok\ncapture dark: ok' });
  });
}

for (const theme of ['light', 'dark'] as const) {
test(`Activity renders structured tool payloads and runner events quietly in ${theme} theme`, async ({ page }) => {
  const fullImageRequests: string[] = [];
  page.on('request', (request) => {
    if (request.url().includes(`/api/tasks/${encodeURIComponent(TARGET.id)}/screenshot?`)) {
      fullImageRequests.push(request.url());
    }
  });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.nextGenChat', '1');
  });
  await page.addInitScript((selectedTheme) => {
    localStorage.setItem('atp.studio.theme', selectedTheme);
  }, theme);
  await installRoutes(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(
    `/?job=${encodeURIComponent(TARGET.id)}&watchPath=${encodeURIComponent(TARGET.watchPath)}`,
  );
  await page.getByTestId('inspector-tab-activity').click();

  const panel = page.getByTestId('activity-panel');
  await expect(panel).toBeVisible();
  await expect(panel.getByTestId('conversation-view')).toBeVisible();

  const protectedMessage = panel.getByTestId('conversation-message-item')
    .filter({ hasText: 'docs/rendering-defense.md' });
  await expect(protectedMessage).toHaveCount(1);
  const protectedDiff = protectedMessage.locator('pre[data-lang="diff"]');
  await expect(protectedDiff).toHaveCount(1);
  await expect(protectedDiff).toContainText('diff --git a/docs/rendering-defense.md');
  await expect(protectedDiff).toContainText('<header class="page">');
  await expect(protectedDiff).toContainText('<svg viewBox="0 0 24 24">');
  await expect(protectedMessage.locator('table')).toHaveCount(0);
  await expect(protectedMessage.locator('ul')).toHaveCount(0);
  await expect(protectedDiff.locator('a, app-task-reference-microcard')).toHaveCount(0);
  await expect(protectedMessage.getByRole('link', { name: 'ASS-4242' })).toHaveCount(1);

  const gallery = panel.getByTestId('conversation-artifact-gallery');
  await expect(gallery).toBeVisible();
  const thumbnails = gallery.getByTestId('artifact-gallery-thumbnail');
  await expect(thumbnails).toHaveCount(8);
  await expect(thumbnails.first().locator('img')).toHaveAttribute('loading', 'lazy');
  await expect(thumbnails.first().locator('img')).toHaveAttribute('src', /\/thumbnail\?/);
  await expect(gallery.getByTestId('artifact-document-diff')).toBeVisible();
  await expect(gallery.getByTestId('artifact-document-markdown')).toBeVisible();
  await expect(gallery.getByTestId('artifact-document-json')).toBeVisible();
  await expect(gallery.getByTestId('artifact-document-log')).toBeVisible();
  await gallery.screenshot({
    path: path.join(SHOTS_DIR, `AGT-2558--artifact-gallery-${theme}--mocked.png`),
  });
  expect(fullImageRequests, 'the thumbnail grid must not fetch full-size images').toHaveLength(0);

  await thumbnails.first().click();
  const lightbox = page.getByTestId('media-lightbox');
  await expect(lightbox).toBeVisible();
  await expect(page.getByTestId('media-lightbox-position')).toHaveText('Image 1 / 8');
  await expect(page.getByTestId('media-lightbox-caption')).toHaveText(
    'gallery-dashboard-light.png · results/gallery-dashboard-light.png',
  );
  await expect(page.getByTestId('media-lightbox-action-open-new-tab')).toBeVisible();
  await page.keyboard.press('ArrowRight');
  await expect(page.getByTestId('media-lightbox-position')).toHaveText('Image 2 / 8');
  await expect(page.getByTestId('media-lightbox-caption')).toHaveText(
    'gallery-dashboard-dark.png · results/gallery-dashboard-dark.png',
  );
  await page.keyboard.press('Escape');
  await expect(lightbox).toHaveCount(0);

  await gallery.getByTestId('artifact-document-toggle-diff').click();
  await expect(gallery.getByTestId('artifact-document-preview')).toContainText('typed gallery export');

  const tools = panel.getByTestId('tool-burst-chip');
  await expect(tools).toHaveCount(4);
  const mixedTool = tools.nth(0);
  const editTool = tools.nth(1);
  if (EVIDENCE_VARIANT === 'before') {
    await expect(mixedTool.getByTestId('tool-burst-row')).toContainText('6');
    await expect(mixedTool.getByTestId('tool-burst-row')).not.toContainText('shell ×4');
    await expect(editTool.getByTestId('tool-burst-row')).toContainText('5');
    await expect(editTool.getByTestId('tool-burst-row')).not.toContainText('5 Edits · 1 file');
  } else {
    await expect(mixedTool.getByTestId('tool-burst-row')).toContainText('6 Tool calls');
    await expect(mixedTool.getByTestId('tool-burst-row')).toContainText('shell ×4, read ×2');
    await expect(mixedTool.getByTestId('tool-burst-row')).toContainText('all ok');
    await expect(mixedTool.getByTestId('tool-burst-row')).toContainText('10s');
    await expect(editTool.getByTestId('tool-burst-row')).toContainText('5 Edits · 1 file');
    await expect(editTool.getByTestId('tool-burst-row')).toContainText('frontend/src/app/campaign.ts');
    await expect(editTool.getByTestId('tool-burst-row')).toContainText('8s');
    await expect(editTool.getByTestId('activity-edit-files')).toHaveAttribute(
      'title',
      `${WORKTREE_ROOT}/frontend/src/app/campaign.ts`,
    );
    await expect(editTool.getByRole('button', { name: /diff/i })).toHaveCount(0);
  }
  await panel.getByTestId('conversation-view').screenshot({
    path: path.join(
      SHOTS_DIR,
      process.env['JOB_RESULTS_DIR']
        ? `AGT-2526--tool-edit-lines-${EVIDENCE_VARIANT}-${theme}--mocked.png`
        : `activity-density-${EVIDENCE_VARIANT}-${theme}.png`,
    ),
  });
  const diffTool = tools.nth(2);
  await diffTool.getByTestId('tool-burst-row').click();
  const diffOutput = diffTool.getByTestId('tool-burst-command-output');
  await expect(diffOutput).toContainText('diff --git a/docs/start/README.md');
  await expect(diffOutput).toContainText('"title": "Apply Robert\'s selected Deck icon"');
  expect((await panel.getByTestId('conversation-message-item').allTextContents()).join('\n'))
    .not.toContain('diff --git a/docs/start/README.md');
  await panel.screenshot({
    path: path.join(
      SHOTS_DIR,
      theme === 'light' ? 'activity-completion-after.png' : 'activity-completion-after-dark.png',
    ),
  });

  const markupTool = tools.nth(3);
  await expect(markupTool.getByTestId('tool-burst-row')).toHaveAttribute('aria-expanded', 'false');
  await markupTool.getByTestId('tool-burst-row').click();
  const markupOutput = markupTool.getByTestId('tool-burst-command-output');
  await expect(markupOutput).toContainText('<meta charset="utf-8">');
  await expect(markupOutput).toContainText('<p class="lead">');
  await expect(markupTool.getByTestId('tool-burst-files')).toContainText(
    'docs/concepts/wiki-concept.html',
  );
  await expect(markupOutput.locator('li')).toHaveCount(0);
  expect((await panel.getByTestId('conversation-message-item').allTextContents()).join('\n'))
    .not.toContain('<meta charset=');
  await panel.screenshot({
    path: path.join(SHOTS_DIR, 'activity-html-after.png'),
  });
  const runnerRows = panel.locator('[data-testid="conversation-system-status"][data-category="runner"]');
  await expect(runnerRows).toHaveCount(2);
  await expect(runnerRows.first()).not.toContainText('[runner]');
  const completion = panel.locator('[data-testid="conversation-system-status"][data-category="result"]');
  await expect(completion).toHaveCount(1);
  await expect(completion).toContainText('Task complete');
  await expect(completion).toContainText('Outcome ExplicitAgentDone');
  await expect(completion).toContainText('Exit 0');
  await expect(completion.getByRole('button', { name: 'trace' })).toBeVisible();
  await expect(panel).not.toContainText('Runner finished');
  await expect(panel).not.toContainText('[runner-log-delivery:');
});
}

test('Activity, Task, and Result links bind task artifacts to the open card', async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.theme', 'light');
    localStorage.setItem('atp.flag.nextGenChat', '1');
  });
  await installRoutes(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(
    `/?job=${encodeURIComponent(TARGET.id)}&watchPath=${encodeURIComponent(TARGET.watchPath)}`,
  );
  const artifactHref = `/api/tasks/${encodeURIComponent(TARGET.id)}/results/report.html?watchPath=${encodeURIComponent(TARGET.watchPath)}`;

  await page.getByTestId('inspector-tab-activity').click();
  const activityReport = page.getByTestId('activity-panel').getByRole('link', {
    name: 'Konzeptbericht öffnen',
  });
  await expect(activityReport).toHaveAttribute('href', artifactHref);
  await expect(activityReport).toHaveAttribute('target', '_blank');
  const activityPopupPromise = page.waitForEvent('popup');
  await activityReport.click();
  const activityPopup = await activityPopupPromise;
  await expect(activityPopup.getByRole('heading', { name: 'Artifact opened' })).toBeVisible();
  await activityPopup.close();

  await page.getByTestId('inspector-tab-task').click();
  const taskReport = page.getByTestId('task-tab-content').getByRole('link', { name: 'Task report' });
  await expect(taskReport).toHaveAttribute('href', artifactHref);
  await expect(taskReport).toHaveAttribute('target', '_blank');

  await page.getByTestId('inspector-tab-protocol').click();
  const result = page.getByTestId('protocol-beautiful-results');
  await expect(result).toBeVisible();
  const wiki = result.getByRole('link', { name: 'Open quality/angular-components.md in project Wiki' });
  const report = result.getByRole('link', { name: 'Open task artifact results/report.html' });
  const card = result.getByRole('link', { name: 'Open task ASS-4242' });
  const external = result.getByRole('link', { name: 'External' });
  await expect(wiki).toBeVisible();
  await expect(report).toBeVisible();
  await expect(report).toHaveAttribute('href', artifactHref);
  await expect(report).toHaveAttribute('target', '_blank');
  await expect(card).toBeVisible();
  await expect(external).toHaveAttribute('target', '_blank');
  await page.screenshot({
    path: path.join(SHOTS_DIR, 'result-links-in-app-after.png'),
    fullPage: false,
  });

  const resultPopupPromise = page.waitForEvent('popup');
  await report.click();
  const resultPopup = await resultPopupPromise;
  await expect(resultPopup.getByRole('heading', { name: 'Artifact opened' })).toBeVisible();
  await resultPopup.close();

  await card.click();
  await expect(page).toHaveURL(/#\/tasks\/ASS-4242/);

  await result.getByRole('link', { name: 'Open quality/angular-components.md in project Wiki' }).click();
  await expect(page).toHaveURL(/#\/projects\/fixture\/wiki/i);
  await expect.poll(() => page.evaluate(() => {
    const value = localStorage.getItem('atp.projectWiki.v1.fixture');
    return value ? JSON.parse(value).openedRel : null;
  })).toBe('quality/angular-components.md');
});
