import { expect, Page, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const WATCH_PATH = 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard';
const SHOTS_DIR = process.env['JOB_RESULTS_DIR']
  ?? resolve(__dirname, '../../test-results/result-scaffold-presentation');

interface ScaffoldFixture {
  id: string;
  key: string;
  title: string;
  state: string;
  mode: 'coding' | 'planning';
  enteredLaneAt: string;
  lastActivity: string;
  artifact: string;
  transitionLine: string;
  operatorBackfill?: boolean;
}

const FIXTURES: ScaffoldFixture[] = [
  {
    id: 'konzept-kontext-bezogene-orchestrator-chats',
    key: 'AGT-2514',
    title: 'Context-aware orchestrator chats',
    state: '5-human-review',
    mode: 'planning',
    enteredLaneAt: '2026-08-08T21:52:18.989Z',
    lastActivity: '2026-08-08T21:59:26.180Z',
    artifact: 'results/report.html',
    transitionLine: 'The task reached `5-human-review`.',
  },
  {
    id: 'board-neues-deck-icon',
    key: 'AGT-2355',
    title: 'Board: new Deck icon alternatives',
    state: '5-human-review',
    mode: 'coding',
    enteredLaneAt: '2026-07-28T21:05:34.570Z',
    lastActivity: '2026-08-09T16:00:17.677Z',
    artifact: 'results/deliverables.md',
    transitionLine: 'A transition is pending into `6-completed`.',
    operatorBackfill: true,
  },
];

function info(fixture: ScaffoldFixture) {
  return {
    id: fixture.id,
    key: fixture.key,
    displayKey: fixture.key,
    taskKey: `${WATCH_PATH}::${fixture.id}`,
    title: fixture.title,
    state: fixture.state,
    mode: fixture.mode,
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    model: 'gpt-5.6-sol',
    thinkingLevel: 'xhigh',
    watchPath: WATCH_PATH,
    projectName: 'Agent Studio',
    folderPath: `${WATCH_PATH}/tasks/${fixture.key}`,
    createdAt: '2026-08-08T19:34:52.776Z',
    enteredLaneAt: fixture.enteredLaneAt,
    lastActivity: fixture.lastActivity,
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    codeActivityDetected: false,
    orchestratorVerdict: 'accept',
    taskType: 'feature',
    tags: [],
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
  };
}

function statusMarkdown(fixture: ScaffoldFixture): string {
  const backfillMarker = fixture.operatorBackfill
    ? '<!-- agent-studio:operator-result-backfill -->\n'
    : '';
  return `<!-- agent-studio:result-scaffold -->
${backfillMarker}# Status

- Result: Success
- Case: generic
- Grade: Not recorded
- Deliverables: [${fixture.artifact}](${fixture.artifact})
- Integration: \`no-branch\` on \`develop\`
- Provenance: Synthesized by Agent Studio because no generated status.md was available.

## Overview

- Problem: \`status.md\` was missing for task \`${WATCH_PATH}::${fixture.id}\`.
- Solution: This honest scaffold exposes the recorded outcome and existing evidence for ${fixture.title}.

## What Was Done

- ${fixture.transitionLine}
- Grade, deliverables, and integration facts are linked or stated above when available.

## Open Items

- None recorded in this synthesized scaffold.
`;
}

function detail(fixture: ScaffoldFixture) {
  return {
    info: info(fixture),
    promptMarkdown: 'Fixture prompt.',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: statusMarkdown(fixture),
    statusGeneration: null,
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }));
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], codeNotComplete: [], autoReview: [],
        humanReview: FIXTURES.map(info), escalated: [], review: [], completed: [], archive: [],
      }),
    }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: 'Agent Studio', path: WATCH_PATH, rootPath: WATCH_PATH }]),
    }));
  await page.route('**/api/v1/projects', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 'agent-studio', displayName: 'Agent Studio', shortCode: 'AGT',
        workspaceId: 'workspace-agent-studio', storageLocation: WATCH_PATH,
        rootPath: WATCH_PATH, repositoryPath: WATCH_PATH, sortOrder: 0, archived: false, urls: [],
      }]),
    }));
  await page.route('**/api/projects/*/workbenches**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projectName: 'Agent Studio',
        includesHistory: true,
        count: 0,
        items: [],
      }),
    }));
  await page.route('**/api/v1/workspaces', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ snapshots: [], ttlSeconds: 600 }),
    }));
  await page.route('**/api/v1/studio/runner/status**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          'Agent Studio': {
            projectName: 'Agent Studio', mode: 'manual', activeJobId: null,
            activeExecution: null, queuedJobIds: [],
          },
        },
      }),
    }));

  for (const fixture of FIXTURES) {
    const id = encodeURIComponent(fixture.id);
    await page.route(`**/api/tasks/${id}/output**`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
    await page.route(`**/api/tasks/${id}/pipeline**`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          pipeline: { pre: [], core: [], post: [], allSteps: [] },
          execution: null, executions: [], config: {}, cost: null,
        }),
      }));
    await page.route(`**/api/tasks/${id}/runs**`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ runs: [], runnerEvents: [] }),
      }));
    await page.route(`**/api/tasks/${id}/session-events**`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ events: [], sessionChain: [] }),
      }));
    await page.route(`**/api/tasks/${id}/claude-session**`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
    await page.route(`**/api/tasks/${id}/artifacts**`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ jobId: fixture.id, files: [] }),
      }));
    await page.route(`**/api/v1/projects/*/tasks/${id}?**`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(detail(fixture)),
      }));
  }
}

test.describe('Result scaffold presentation', () => {
  test.use({ serviceWorkers: 'block' });

  for (const theme of ['light', 'dark'] as const) {
    test(`AGT-2514 and a second marked scaffold render one understandable origin notice in ${theme} theme`, async ({ page }) => {
      await page.addInitScript((selectedTheme) => {
        localStorage.setItem('atp.studio.theme', selectedTheme);
      }, theme);
      await installRoutes(page);
      await page.setViewportSize({ width: 1440, height: 900 });
      mkdirSync(SHOTS_DIR, { recursive: true });

      for (const fixture of FIXTURES) {
        await page.goto(
          `/?job=${encodeURIComponent(fixture.id)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
          { waitUntil: 'domcontentloaded' },
        );
        const resultTab = page.getByTestId('inspector-tab-protocol');
        if (await resultTab.getAttribute('aria-selected') !== 'true') await resultTab.click();

        const result = page.getByTestId('protocol-beautiful-results');
        const notice = result.getByTestId('result-scaffold-notice');
        await expect(notice).toBeVisible();
        await expect(notice).toContainText(
          'The run did not write status.md. This report was generated automatically from task.json and the artifacts.',
        );
        await expect(notice).toContainText(fixture.key);
        await expect(notice.getByRole('link', { name: 'Open artifacts' })).toBeVisible();
        await expect(result).toContainText('What Was Done');
        await expect(result).toContainText('Open Items');
        await expect(result).not.toContainText('Question');
        await expect(result).not.toContainText('Written');
        await expect(result).not.toContainText('This honest scaffold exposes');
        await expect(result).not.toContainText('agent-studio:result-scaffold');
        await expect(result).not.toContainText('agent-studio:operator-result-backfill');
        await expect(result).not.toContainText('C:/Projects');
        await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);

        if (fixture.key === 'AGT-2514') {
          await page.getByTestId('pane-protocol').screenshot({
            path: join(
              SHOTS_DIR,
              theme === 'light'
                ? 'agt-2514-result-scaffold-origin--mocked.png'
                : 'agt-2514-result-scaffold-origin-dark--mocked.png',
            ),
          });
        }
      }
    });
  }
});
