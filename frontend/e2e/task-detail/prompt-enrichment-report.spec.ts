import { expect, test, type Page, type TestInfo } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import * as path from 'node:path';

const JOB_ID = 'prompt-enrichment-fixture';
const WATCH_PATH = '/fixtures/prompt-enrichment';
const EVIDENCE_PHASE = process.env['EVIDENCE_PHASE'] === 'before' ? 'before' : 'after';

async function screenshotPath(
  testInfo: TestInfo,
  theme: 'light' | 'dark',
  viewport: 'wide' | 'narrow',
): Promise<string> {
  const configured = process.env['JOB_RESULTS_DIR']?.trim();
  const resultsDir = configured ? path.resolve(configured) : testInfo.outputDir;
  await mkdir(resultsDir, { recursive: true });
  return path.join(
    resultsDir,
    `mkt-17-task-layout-${EVIDENCE_PHASE}-${viewport}-${theme}--mocked.png`,
  );
}

function json(body: unknown) {
  return {
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  };
}

function detail() {
  return {
    info: {
      id: JOB_ID,
      taskKey: `MKT-E2E-${JOB_ID}`,
      displayKey: 'MKT-17',
      title: 'Community-Layer ausrollen (GitHub-Publikation) - gesperrt bis Freigabe',
      state: '3-progress',
      order: 1,
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5.6-sol',
      thinkingLevel: 'high',
      watchPath: WATCH_PATH,
      projectName: 'Agent Taskboard',
      folderPath: `${WATCH_PATH}/3-progress/${JOB_ID}`,
      createdAt: '2026-07-28T09:00:00Z',
      lastActivity: '2026-07-28T09:01:00Z',
      sessionName: null,
      useOwnSession: null,
      lastUsage: null,
      execution: null,
      commit: null,
      commits: [],
      tags: ['frontend', 'delegation'],
      references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    },
    promptMarkdown: [
      '# Community-Layer ausrollen (GitHub-Publikation) - GESPERRT bis Robert freigibt',
      '',
      'Nachfolgekarte zu MKT-6. Der lokale Teil ist geliefert und verifiziert. Offen ist nur noch das, was nach aussen wirkt. Genau dafuer ist diese Karte da.',
      '',
      '## HARTER OPERATOR-GUARD',
      '',
      'Diese Karte darf nichts nach aussen publizieren, solange Robert nicht ausdruecklich und pro Punkt freigegeben hat. Kein Agent fuehrt hier eigenmaechtig `gh issue create`, `gh api`, Discussions-Aktivierung oder Board-Anlage aus.',
      '',
      '## Erlaubte Vorbereitung (ohne Freigabe)',
      '',
      '1. Trockenlauf-Plan je Ziel-Repo: welches Issue mit welchem Titel/Body/Labels in welchem Repo, als Datei in `results/` - nicht als API-Aufruf.',
      '2. Pruefen, ob `gh` auf dem ausfuehrenden Host vorhanden und authentifiziert ist.',
      '3. Kollisionspruefung: existieren Titel/Labels in den Ziel-Repos schon?',
      '4. Reihenfolge- und Rollback-Vorschlag (was zuerst, was ist reversibel).',
      '',
      '## Vorzulegende Entscheidung (Robert)',
      '',
      '- [ ] Issues publizieren - in welchen Repos, welche der 8?',
      '- [ ] Discussions auf agent-orc/agent-studio aktivieren?',
      '- [ ] Oeffentliches Projekt-Board anlegen und verlinken?',
      '- [ ] Board-IDs an die Kandidaten-Issues vergeben?',
      '',
      '```bash',
      'gh issue create --repo agent-orc/agent-studio --title "Community contribution path with a deliberately long dry-run title" --body-file results/community-layer-rollout-dry-run-with-review-notes.md --label good-first-issue,community,documentation',
      '```',
    ].join('\n'),
    enrichmentReport: {
      schemaVersion: '1.0',
      enrichmentId: 'e2e-enrichment',
      generatedAtUtc: '2026-07-28T09:00:02Z',
      status: 'enriched',
      originalPromptSha256: 'aaa',
      enrichedPromptSha256: 'bbb',
      policy: {
        id: 'prompt-enrichment',
        version: '1',
        projectEnabled: true,
        selector: 'constraint-selector-v4-token-economy',
        tokenizer: 'character-estimate-v1',
        tokenBudget: 1500,
        optionalBlockLimit: 2,
        styleGuideSnapshotId: 'style-snapshot-42',
      },
      detectedAreas: ['frontend', 'task-detail'],
      candidates: [
        {
          id: 'repo-instructions-source',
          title: 'Use repository instructions and indexed docs',
          source: 'AGENTS.md; docs/start/README.md',
          signals: ['general'],
          decision: 'appended',
          reason: 'mandatory-project-policy',
          estimatedTokens: 78,
        },
        {
          id: 'style-guide:angular-components',
          title: 'Angular component guide',
          source: 'docs/quality/angular-components.md',
          signals: ['frontend'],
          decision: 'appended',
          reason: 'matched-task-area',
          estimatedTokens: 64,
        },
        {
          id: 'style-guide:frontend-styling',
          title: 'Frontend styling context',
          source: 'docs/quality/frontend-styling.md',
          signals: ['frontend'],
          decision: 'appended',
          reason: 'matched-task-area',
          estimatedTokens: 72,
        },
      ],
      appendedBlocks: [
        {
          id: 'repo-instructions-source',
          title: 'Use repository instructions and indexed docs',
          source: 'AGENTS.md; docs/start/README.md',
          revision: '1',
          digestSha256: 'ccc',
          tier: 'mandatory-project-policy',
          order: 1,
          estimatedTokens: 78,
          exactContent:
            '- **Use repository instructions and indexed docs** (`repo-instructions-source`)',
        },
        {
          id: 'style-guide:angular-components',
          title: 'Angular component guide',
          source: 'docs/quality/angular-components.md',
          revision: '1',
          digestSha256: 'ddd',
          tier: 'optional',
          order: 2,
          estimatedTokens: 64,
          exactContent: '- **Angular component guide** (`style-guide:angular-components`)',
        },
        {
          id: 'style-guide:frontend-styling',
          title: 'Frontend styling context',
          source: 'docs/quality/frontend-styling.md',
          revision: '7',
          digestSha256: 'eee',
          tier: 'optional',
          order: 3,
          estimatedTokens: 72,
          exactContent: '- **Frontend styling context** (`style-guide:frontend-styling`)',
        },
      ],
      tokens: {
        tokenizer: 'character-estimate-v1',
        original: 542,
        appended: 340,
        final: 882,
        preprocessingInput: 0,
        preprocessingOutput: 0,
        preprocessingCacheRead: 0,
        preprocessingCacheCreation: 0,
      },
      cost: {
        currency: 'USD',
        selectorUsd: 0,
        appendedInputUsd: 0.0007,
        estimateModel: 'gpt-5.6-sol',
      },
      timingMs: 4,
      warnings: ['One optional source was skipped because the block limit was reached.'],
      errors: [],
    },
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: null,
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

async function installRoutes(page: Page): Promise<void> {
  const encodedId = encodeURIComponent(JOB_ID);
  await page.route('**/api/**', (route) => route.fulfill(json([])));
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill(
      json({
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      }),
    ),
  );
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill(
      json({
        backlog: [],
        preparation: [],
        orchestratorPrep: [],
        ready: [],
        progress: [detail().info],
        failedPickup: [],
        codeNotComplete: [],
        autoReview: [],
        humanReview: [],
        review: [],
        completed: [],
        archive: [],
      }),
    ),
  );
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill(json([{ name: 'Agent Taskboard', path: WATCH_PATH, rootPath: WATCH_PATH }])),
  );
  await page.route('**/api/v1/studio/runner/status**', (route) => route.fulfill(json({ projects: {} })));
  await page.route('**/api/projects/*/cli-modes', (route) =>
    route.fulfill(
      json({
        resolved: {},
        overrides: {},
        available: [],
      }),
    ),
  );
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill(
      json({
        snapshots: [],
        ttlSeconds: 600,
      }),
    ),
  );
  await page.route('**/api/tasks/reference-status', (route) => route.fulfill(json({ items: [] })));
  await page.route(`**/api/tasks/${encodedId}/runs**`, (route) =>
    route.fulfill(json({ runs: [], runnerEvents: [] })),
  );
  await page.route(`**/api/tasks/${encodedId}/session-events**`, (route) =>
    route.fulfill(json({ events: [], sessionChain: [] })),
  );
  await page.route(`**/api/tasks/${encodedId}/pipeline**`, (route) =>
    route.fulfill(
      json({
        pipeline: { pre: [], core: [], post: [], allSteps: [] },
        execution: null,
        executions: [],
        config: {},
        cost: null,
      }),
    ),
  );
  await page.route(`**/api/v1/projects/*/tasks/${encodedId}?**`, (route) => route.fulfill(json(detail())));
}

for (const theme of ['light', 'dark'] as const) {
  for (const viewport of ['wide', 'narrow'] as const) {
    test(`keeps the MKT-17 prompt readable with compact enrichment (${viewport}, ${theme})`, async ({
      page,
    }, testInfo) => {
      await page.addInitScript((selectedTheme) => {
        localStorage.setItem('atp.studio.theme', selectedTheme);
        document.documentElement.dataset['studioTheme'] = selectedTheme;
        if (!sessionStorage.getItem('e2e.enrichmentReport.initialized')) {
          sessionStorage.removeItem('taskboard.taskInspector.enrichmentExpanded');
          sessionStorage.setItem('e2e.enrichmentReport.initialized', '1');
        }
      }, theme);
      await installRoutes(page);
      await page.setViewportSize({
        width: viewport === 'wide' ? 1600 : 960,
        height: viewport === 'wide' ? 1000 : 820,
      });
      await page.goto(
        `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
      );

      await page.getByTestId('inspector-tab-task').click();
      const prompt = page.getByTestId('task-tab-prompt');
      const report = page.getByTestId('enrichment-report');
      await expect(prompt).toContainText('Community-Layer ausrollen');
      await expect(prompt).toContainText('HARTER OPERATOR-GUARD');
      await expect(report).toBeVisible();
      await expect(report).toContainText('Enriched');

      await page.screenshot({
        path: await screenshotPath(testInfo, theme, viewport),
        fullPage: false,
      });
      if (EVIDENCE_PHASE === 'before') return;

      const summary = page.getByTestId('enrichment-report-toggle');
      await expect(summary).toHaveAttribute('aria-expanded', 'false');
      await expect(summary).toContainText('542 +340 → 882');
      await expect(summary).toContainText('3 decisions');
      await expect(summary).toContainText('3 appended');
      await expect(summary).toContainText('1 message');
      await expect(page.getByTestId('enrichment-report-details')).toHaveCount(0);

      const promptBox = await prompt.boundingBox();
      const reportBox = await report.boundingBox();
      expect(promptBox).not.toBeNull();
      expect(reportBox).not.toBeNull();
      expect(promptBox!.width).toBeGreaterThan(Math.min(reportBox!.width * 0.72, 720));
      expect(promptBox!.x).toBe(reportBox!.x);
      expect(promptBox!.y).toBeGreaterThan(reportBox!.y + reportBox!.height - 1);
      expect(
        await page
          .getByTestId('task-tab-content')
          .evaluate((element) => element.scrollWidth <= element.clientWidth + 1),
      ).toBe(true);
      expect(
        await prompt
          .locator('pre')
          .evaluate((element) => element.scrollWidth > element.clientWidth),
      ).toBe(true);

      await summary.click();
      await expect(summary).toHaveAttribute('aria-expanded', 'true');
      const details = page.getByTestId('enrichment-report-details');
      await expect(details).toContainText('Enrichment report');
      await expect(details).toContainText('Original542');
      await expect(details).toContainText('Appended+340');
      await expect(details).toContainText('Final882');
      await expect(details).toContainText('Selector: 0 tokens');
      await expect(details).toContainText('Angular component guide');
      const detailsBox = await details.boundingBox();
      expect(detailsBox).not.toBeNull();
      expect(Math.abs(detailsBox!.width - reportBox!.width)).toBeLessThanOrEqual(2);
      expect(
        await page
          .getByTestId('task-tab-content')
          .evaluate((element) => element.scrollWidth <= element.clientWidth + 1),
      ).toBe(true);

      await page.goto(
        `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
      );
      await page.getByTestId('inspector-tab-task').click();
      await expect(page.getByTestId('enrichment-report-toggle')).toHaveAttribute(
        'aria-expanded',
        'true',
      );
    });
  }
}
