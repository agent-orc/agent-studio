import { expect, test } from '../fixtures/dev-backend';
import type { Page, Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Action Bar Evidence';
const WATCH_PATH = 'C:/evidence/action-bar';
const DOC_PATH = 'README.md';
const CONCEPT_PATH = 'concepts/page-actions.md';
const WORKBENCH_PATH = 'quality/action-bar-workbench/index.html';
const WORKBENCH_ID = 'action-bar-workbench';
const RESULTS = process.env.JOB_RESULTS_DIR
  ? resolve(process.env.JOB_RESULTS_DIR)
  : resolve(__dirname, '..', '..', '..', 'results', 'AGT-2282');

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
  escalated: [], review: [], completed: [], archive: [],
};

const pages: Record<string, string> = {
  [DOC_PATH]: '# Action bar contract\n\nEvery repository page exposes the same dependable actions in its page head.',
  [CONCEPT_PATH]: '# Pages as interfaces\n\nAIP-4 treats each page as a bidirectional interface between knowledge and delivery.',
  [WORKBENCH_PATH]: `<main>
    <h1>Action Bar Dossier</h1>
    <p>Compare the shared actions in both themes.</p>
    <section data-decision-id="action-density" data-decision-kind="single">
      <h2>Choose the action density</h2>
      <ul>
        <li data-option-id="compact">Compact controls in one row</li>
        <li data-option-id="expanded">Expanded controls with descriptions</li>
      </ul>
      <label>Optional note <textarea data-comment="Optional action-density note"></textarea></label>
    </section>
  </main>`,
};

interface CapturedCalls {
  taskBodies: Record<string, unknown>[];
  chatBodies: Record<string, unknown>[];
  archivePaths: string[];
  pinBodies: Record<string, unknown>[];
  decisionBodies: Record<string, unknown>[];
}

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installMocks(page: Page): Promise<CapturedCalls> {
  const captured: CapturedCalls = {
    taskBodies: [],
    chatBodies: [],
    archivePaths: [],
    pinBodies: [],
    decisionBodies: [],
  };
  let created = false;
  let workbenchTaskCreated = false;
  let pinnedPath: string | null = null;

  // Register the broad fallback first. Playwright gives later routes priority.
  await page.route('**/api/**', route => json(route, []));

  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local',
    bootstrapRequired: false,
    authenticated: true,
    user: null,
  }));
  await page.route('**/api/watch-paths', route => json(route, [{
    name: PROJECT,
    path: WATCH_PATH,
    rootPath: WATCH_PATH,
    repositoryPath: WATCH_PATH,
  }]));
  await page.route('**/api/v1/workspaces**', route => json(route, [{
    id: 'ws-evidence',
    displayName: 'Evidence',
    sortOrder: 0,
    isDefault: true,
    projects: [{
      id: 'project-evidence',
      displayName: PROJECT,
      shortCode: 'ABE',
      workspaceId: 'ws-evidence',
      storageLocation: WATCH_PATH,
      sortOrder: 0,
      archived: false,
      urls: [],
    }],
  }]));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/v1/studio/runner/status**', route => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-07-23T10:00:00Z',
    snapshots: [],
    ttlSeconds: 600,
  }));
  await page.route('**/api/cli/usage**', route => json(route, {
    at: '2026-07-23T10:00:00Z',
    sessions: [],
  }));
  await page.route(/\/api\/cli\/[^/]+\/models(?:\?.*)?$/, route => json(route, {
    models: [],
    source: 'page-action-evidence',
  }));
  await page.route('**/api/cli/maintenance-model', route => json(route, {
    cliType: 'codex',
    model: 'gpt-5',
    thinkingLevel: null,
  }));
  await page.route('**/api/cli/model-routing/recommendation**', route => json(route, {
    model: 'claude-opus-4-6',
    thinkingLevel: null,
    tier: 'complex',
    taskType: 'chore',
    economyDowngraded: false,
    policyVersion: 'e2e',
    policyWikiPath: 'docs/system/domains/model-routing-policy.md',
  }));
  await page.route('**/api/crash-recovery/pending**', route => json(route, { pending: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, {
    items: [],
    total: 0,
    offset: 0,
    limit: 50,
  }));
  await page.route('**/api/tasks/reference-status', route => json(route, {
    items: workbenchTaskCreated ? [{
      key: 'AGT-2400', exists: true, taskKey: `${PROJECT}::AGT-2400`,
      title: 'Implement Action Bar Dossier', lane: '1-preparation',
      projectId: 'project-evidence', projectName: PROJECT, projectColor: null,
      merge: null, reviewGrade: null,
    }] : [],
  }));
  await page.route('**/api/v1/studio/board**', route => json(route, created
    ? {
        ...EMPTY_GROUPED,
        preparation: [{
          id: 'page-task-1',
          displayKey: 'ABE-1',
          taskKey: `${PROJECT}::page-task-1`,
          title: 'Task from page: Action bar contract',
          state: '1-preparation',
          projectName: PROJECT,
          watchPath: WATCH_PATH,
        }, ...(workbenchTaskCreated ? [{
          id: 'workbench-feature-1', key: 'AGT-2400', displayKey: 'AGT-2400',
          taskKey: `${PROJECT}::AGT-2400`, title: 'Implement Action Bar Dossier',
          state: '1-preparation', projectName: PROJECT, watchPath: WATCH_PATH,
        }] : [])],
      }
    : EMPTY_GROUPED));
  await page.route(/\/api\/tasks(?:\?.*)?$/, route => {
    if (route.request().method() === 'POST') {
      const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;
      captured.taskBodies.push(body);
      created = true;
      if (String(body['title']).includes('Action Bar Dossier')) {
        workbenchTaskCreated = true;
        return json(route, { id: 'workbench-feature-1' });
      }
      return json(route, { id: 'page-task-1' });
    }
    return json(route, created ? [{
      id: 'page-task-1',
      displayKey: 'ABE-1',
      taskKey: `${PROJECT}::page-task-1`,
      title: 'Task from page: Action bar contract',
      state: '1-preparation',
      projectName: PROJECT,
      watchPath: WATCH_PATH,
    }] : []);
  });
  await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/workbench-feature-1(?:\?.*)?$/, route => json(route, {
    info: {
      id: 'workbench-feature-1', key: 'AGT-2400', displayKey: 'AGT-2400',
      taskKey: `${PROJECT}::AGT-2400`, title: 'Implement Action Bar Dossier',
      state: '1-preparation', projectName: PROJECT, watchPath: WATCH_PATH,
    },
  }));

  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/wiki/tree`, route => json(route, {
    projectName: PROJECT,
    baseDir: `${WATCH_PATH}/docs`,
    exists: true,
    root: [
      {
        name: DOC_PATH,
        title: 'Action bar contract',
        relPath: DOC_PATH,
        type: 'md',
        children: [],
        classification: {
          status: 'aktuell',
          type: 'runbook',
          pageType: 'doc',
          supersededBy: null,
          analyzedAt: '2026-07-23',
        },
      },
      {
        name: 'concepts',
        title: 'Concepts',
        relPath: 'concepts',
        type: 'folder',
        children: [{
          name: 'page-actions.md',
          title: 'Pages as interfaces',
          relPath: CONCEPT_PATH,
          type: 'md',
          children: [],
          classification: {
            status: 'aktuell',
            type: 'konzept',
            pageType: 'concept',
            supersededBy: null,
            analyzedAt: '2026-07-23',
          },
        }],
      },
      {
        name: 'quality',
        title: 'Quality',
        relPath: 'quality',
        type: 'folder',
        children: [{
          name: 'action-bar-workbench',
          title: 'Action Bar Dossier',
          relPath: WORKBENCH_PATH,
          type: 'html',
          children: [],
          classification: {
            status: 'aktuell',
            type: 'workbench',
            pageType: 'workbench',
            supersededBy: null,
            analyzedAt: '2026-07-23',
          },
        }],
      },
    ],
  }));
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/style-guides`, route => json(route, {
    projectKey: 'ABE',
    projectDisplayName: PROJECT,
    technologies: [],
    guides: [],
    warnings: [],
    snapshotId: 'action-bar-evidence',
    capturedAtUtc: '2026-07-23T10:00:00Z',
    refreshAfterUtc: '2026-07-23T11:00:00Z',
  }));
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/wiki/home`, route => json(route, {
    sections: [
      {
        title: 'Start',
        links: pinnedPath ? [{
          relPath: pinnedPath,
          label: 'Action bar contract',
          note: 'Shared page action entry.',
          exists: true,
        }] : [],
      },
      { title: 'UI & Wiki', links: [] },
    ],
  }));
  await page.route(/\/api\/projects\/[^/]+\/wiki\/home\/pins\/(.+)$/, route => {
    const relPath = decodeURIComponent(new URL(route.request().url()).pathname.split('/wiki/home/pins/')[1]);
    const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;
    captured.pinBodies.push({ relPath, ...body });
    pinnedPath = body['pinned'] === true ? relPath : null;
    return json(route, { relPath: 'docs/app/config/home.json', sha: 'pin-evidence' });
  });
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/wiki/grading/status**`, route => json(route, {
    status: null,
  }));
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/wiki/pulse**`, route => json(route, {
    projectName: PROJECT,
    baseDir: `${WATCH_PATH}/docs`,
    exists: true,
    generatedAtUtc: '2026-07-23T10:00:00Z',
    feed: { available: true, reason: null, items: [] },
    inbox: { available: true, reason: null, count: 0, items: [] },
    drift: {
      available: true,
      reason: null,
      overallGrade: 'Fresh',
      areas: [],
      counts: { fresh: 3, aging: 0, stale: 0, graded: 3 },
    },
    critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
    workbenches: {
      projectName: PROJECT,
      includesHistory: false,
      count: 1,
      items: [{
        id: WORKBENCH_ID,
        title: 'Action Bar Dossier',
        summary: 'Canonical variants and theme tokens.',
        status: 'active',
        phase: 'decision-ready',
        updatedAtUtc: '2026-07-23T10:00:00Z',
        entryPath: `docs/${WORKBENCH_PATH}`,
        valid: true,
        error: null,
        sourceTaskKeys: ['AGT-2282'],
      }],
    },
  }));
  await page.route(/\/api\/projects\/[^/]+\/wiki\/files\/(.+)$/, route => {
    const relPath = decodeURIComponent(new URL(route.request().url()).pathname.split('/wiki/files/')[1]);
    return json(route, { relPath, content: pages[relPath] ?? '# Evidence' });
  });
  await page.route(/\/api\/projects\/[^/]+\/wiki\/history\/(.+)$/, route => {
    const relPath = decodeURIComponent(new URL(route.request().url()).pathname.split('/wiki/history/')[1]);
    return json(route, {
      relPath,
      model: 'gpt-5',
      metadata: {
        model: 'gpt-5',
        updatedAt: '2026-07-23',
        reason: 'Action bar evidence',
        taskKey: 'AGT-2282',
        status: 'current',
        runCount: '1',
        hasFrontmatter: true,
      },
      commits: [],
      relatedTasks: [],
    });
  });
  await page.route(/\/api\/projects\/[^/]+\/wiki\/classification\/(.+)$/, route => {
    const relPath = decodeURIComponent(new URL(route.request().url()).pathname.split('/wiki/classification/')[1]);
    captured.archivePaths.push(relPath);
    return json(route, { relPath, sha: 'archive-evidence' });
  });
  let decision: Record<string, unknown> | null = null;
  let decisionStage: string | null = null;
  let revision = '0123456789abcdef';
  let fingerprint = 'a'.repeat(64);
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}/decisions/prepare`,
    route => {
      const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;
      captured.decisionBodies.push(body);
      revision = '1234567890abcdef';
      fingerprint = 'b'.repeat(64);
      decisionStage = 'prepared';
      decision = {
        outcome: body['outcome'],
        state: 'pending',
        operationId: body['operationId'],
        sourceRevision: body['expectedRevision'],
        sourceFingerprint: body['expectedFingerprint'],
        preparedAt: '2026-07-23T10:01:00Z',
        preparedBy: body['actor'],
        confirmedAt: null,
        confirmedBy: null,
        decidedAt: null,
        reason: body['archiveReason'],
        failure: null,
        spawnedTaskKeys: [],
        responses: body['responses'],
        taskDraft: body['task'],
      };
      return json(route, {
        success: true, errorCode: null, error: null, workbenchId: WORKBENCH_ID,
        operationId: body['operationId'], outcome: body['outcome'], decisionStage,
        revision, fingerprint, spawnedTaskKeys: [], responses: body['responses'], idempotent: false,
      });
    },
  );
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}/decisions/confirm`,
    route => {
      const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;
      captured.decisionBodies.push(body);
      revision = '2345678901abcdef';
      fingerprint = 'c'.repeat(64);
      decisionStage = 'succeeded';
      decision = {
        ...decision,
        state: 'succeeded',
        confirmedAt: '2026-07-23T10:02:00Z',
        confirmedBy: body['actor'],
        decidedAt: '2026-07-23T10:02:00Z',
        spawnedTaskKeys: body['spawnedTaskKeys'],
        responses: body['responses'],
        taskDraft: body['task'],
      };
      return json(route, {
        success: true, errorCode: null, error: null, workbenchId: WORKBENCH_ID,
        operationId: body['operationId'], outcome: 'feature-spawn', decisionStage,
        revision, fingerprint, spawnedTaskKeys: body['spawnedTaskKeys'],
        responses: body['responses'], idempotent: false,
      });
    },
  );
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}`, route => json(route, {
    workbench: {
      id: WORKBENCH_ID,
      title: 'Action Bar Dossier',
      summary: 'Canonical variants and theme tokens for page actions.',
      status: decisionStage === 'succeeded' ? 'decided' : decision ? 'decision-pending' : 'active',
      phase: 'decision-ready',
      updatedAtUtc: '2026-07-23T10:00:00Z',
      entryPath: `docs/${WORKBENCH_PATH}`,
      valid: true,
      error: null,
      sourceTaskKeys: ['AGT-2282'],
      lifecycleState: decisionStage === 'succeeded' ? 'decided' : 'review-requested',
      decision,
      decisionStage,
    },
    html: pages[WORKBENCH_PATH],
    branch: 'feature/AGT-2282',
    revision,
    workingTreeModified: false,
    fingerprint,
  }));

  await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, route => {
    const contextKey = decodeURIComponent(
      new URL(route.request().url()).pathname.match(/\/runner\/(.+)\/orchestrator-chat$/)?.[1] ?? '',
    );
    if (route.request().method() === 'POST') {
      captured.chatBodies.push(JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>);
      return json(route, {
        project: PROJECT,
        reply: {
          id: 'reply-1',
          ts: '2026-07-23T10:02:00Z',
          role: 'orchestrator',
          text: 'Page context received.',
        },
      });
    }
    return json(route, { project: contextKey, turns: [] });
  });
  await page.route(/\/api\/v1\/studio\/orchestrator\/context\/project:.+$/, route => json(route, {
    contextKey: `project:${PROJECT}`,
    capturedAt: '2026-07-23T10:00:00Z',
    digest: 'page action evidence',
    sources: [],
  }));

  return captured;
}

async function seedWiki(page: Page) {
  await page.addInitScript(({ project }) => {
    if (localStorage.getItem('atp.studio.tabs.v1')) return;
    const tab = { kind: 'hub', projectName: project, section: 'wiki' };
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [tab],
      activeKey: `hub:${project}:wiki`,
    }));
    localStorage.setItem('atp.studio.theme', 'light');
    localStorage.setItem('defaultCliType', 'claude');
    localStorage.setItem('defaultModel:claude', 'claude-opus-4-6');
  }, { project: PROJECT });
}

async function capture(page: Page, locatorTestId: string, fileName: string) {
  const target = page.getByTestId(locatorTestId);
  await expect(target).toBeVisible();
  const clip = await target.boundingBox();
  expect(clip).not.toBeNull();
  await page.screenshot({ path: resolve(RESULTS, fileName), clip: clip! });
}

test('shared page action bar preserves placement, task source, chat context, and page-type icons', async ({ page, devBackend }) => {
  test.setTimeout(120_000);
  expect(devBackend.port).toBe(Number(process.env['DEV_PORT'] ?? 5030));
  mkdirSync(RESULTS, { recursive: true });
  await page.setViewportSize({ width: 1600, height: 1000 });
  const captured = await installMocks(page);
  await seedWiki(page);
  await page.goto('/');
  await expect(page.getByTestId('project-wiki-section')).toBeVisible({ timeout: 30_000 });
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });

  const docRow = page.getByTestId(`project-wiki-file-${DOC_PATH}`);
  await expect(docRow.locator('[data-page-type]')).toHaveAttribute('data-page-type', 'doc');

  await page.getByTestId('project-wiki-chevron-concepts').click();
  const conceptRow = page.getByTestId(`project-wiki-file-${CONCEPT_PATH}`);
  await expect(conceptRow.locator('[data-page-type]')).toHaveAttribute('data-page-type', 'concept');

  await page.getByTestId('project-wiki-chevron-quality').click();
  const workbenchRow = page.getByTestId(`project-wiki-file-${WORKBENCH_PATH}`);
  await expect(workbenchRow.locator('[data-page-type]')).toHaveAttribute('data-page-type', 'workbench');

  await docRow.click();
  await expect(page.getByTestId('page-action-bar')).toHaveAttribute('data-page-type', 'doc');
  const docBarBox = await page.getByTestId('page-action-bar').boundingBox();
  const docTypeBox = await page.getByTestId('page-action-bar-type').boundingBox();
  const docCreateBox = await page.getByTestId('page-action-create-task').boundingBox();
  expect(docCreateBox!.y).toBeGreaterThanOrEqual(docTypeBox!.y + docTypeBox!.height);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await capture(page, 'project-wiki-reader', `page-action-wiki-doc-${theme}.png`);
  }

  await page.getByTestId('page-action-pin-home').click();
  await expect(page.getByTestId('page-pin-dialog')).toBeVisible();
  await expect(page.getByTestId('page-pin-label')).toHaveValue('Action bar contract');
  await expect(page.getByTestId('page-pin-note')).toHaveValue(
    /Every repository page exposes the same dependable actions/);
  await page.getByTestId('page-pin-section').selectOption('UI & Wiki');
  await page.getByTestId('page-pin-submit').click();
  await expect(page.getByTestId('page-action-pin-home')).toContainText('Unpin from Home');
  expect(captured.pinBodies).toContainEqual(expect.objectContaining({
    relPath: DOC_PATH,
    pinned: true,
    sectionTitle: 'UI & Wiki',
    label: 'Action bar contract',
  }));
  await page.getByTestId('notification-success').getByTestId('notification-close').click();

  await page.getByTestId('page-action-create-task').click();
  const prompt = page.getByTestId('create-prompt');
  await expect(prompt).toHaveValue(new RegExp(`page:${PROJECT}/${DOC_PATH}`));
  await expect(prompt).toHaveValue(/Every repository page exposes the same dependable actions/);
  await page.getByTestId('create-submit').click();
  await expect(page.getByTestId('create-dialog-header')).toHaveCount(0);
  expect(captured.taskBodies).toHaveLength(1);
  expect(String(captured.taskBodies[0]['promptMarkdown'])).toContain(`page:${PROJECT}/${DOC_PATH}`);
  expect(captured.taskBodies[0]['watchPath']).toBe(WATCH_PATH);

  await conceptRow.click();
  await expect(page.getByTestId('page-action-bar')).toHaveAttribute('data-page-type', 'concept');
  const conceptBarBox = await page.getByTestId('page-action-bar').boundingBox();
  expect(conceptBarBox?.x).toBe(docBarBox?.x);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await capture(page, 'project-wiki-reader', `page-action-concept-${theme}.png`);
  }

  await page.getByTestId('page-action-archive').click();
  await expect(page.getByTestId('page-action-archive')).toContainText('Archived');
  expect(captured.archivePaths).toContain(CONCEPT_PATH);
  const notificationClose = page.getByTestId('notification-close');
  while (await notificationClose.first().isVisible().catch(() => false)) {
    await notificationClose.first().click();
  }

  await page.getByTestId('page-action-open-chat').click();
  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
  await page.getByTestId('orch-context-badge').click();
  await expect(page.getByTestId('orch-context-current')).toContainText("concept 'Pages as interfaces'");
  await page.getByTestId('orch-context-badge').click();
  await page.getByTestId('chat-input').fill('Connect this page to delivery.');
  await page.getByTestId('chat-send').click();
  await expect.poll(() => captured.chatBodies.length).toBe(1);
  const navigationContext = captured.chatBodies[0]['navigationContext'] as Record<string, unknown>;
  expect(navigationContext['currentPage']).toBe('repository-page');
  expect(navigationContext['pageRef']).toBe(`page:${PROJECT}/${CONCEPT_PATH}`);
  expect(navigationContext['pageType']).toBe('concept');
  await page.getByTestId('sidesheet-close').click();

  await page.evaluate(({ project, workbenchId }) => {
    const tab = {
      kind: 'workbench',
      projectName: project,
      workbenchId,
      title: 'Action Bar Dossier',
    };
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [tab],
      activeKey: `workbench:${project}:${workbenchId}`,
    }));
  }, { project: PROJECT, workbenchId: WORKBENCH_ID });
  await page.reload();
  const workbenchTab = page.getByRole('tab', { name: 'Action Bar Dossier' });
  if (await workbenchTab.count()) await workbenchTab.click();
  await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });
  const decisionFrame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  const compactOption = decisionFrame.locator('[data-option-id="compact"] input');
  await expect(compactOption).toBeVisible();
  await page.getByTestId('workbench-viewer-details-trigger').click();
  await expect(page.getByTestId('workbench-viewer-details-popover')).toBeVisible();
  await expect(page.getByTestId('workbench-decision-prepare')).toBeDisabled();
  await compactOption.check();
  await decisionFrame.locator('[data-studio-decision-comment]').fill('Keep the header in one compact row.');
  await expect(page.getByTestId('workbench-decision-answer-count')).toContainText('ready');
  await page.getByTestId('workbench-decision-prepare').click();
  await expect(page.getByTestId('workbench-decision-confirm')).toBeVisible();
  await expect(page.getByTestId('workbench-decision-chosen-options')).toContainText('Compact controls');
  await page.getByTestId('workbench-decision-confirm').click();
  const createdTask = page.getByTestId('workbench-decision-created-tasks');
  await expect(createdTask).toContainText('AGT-2400');
  await expect(createdTask).toContainText('Implement Action Bar Dossier');
  await expect(createdTask).toContainText('Preparation');
  await expect(createdTask.locator('a')).toHaveAttribute('href', '#task:AGT-2400');
  expect(captured.decisionBodies).toHaveLength(2);
  expect(captured.decisionBodies[0]).toEqual(expect.objectContaining({
    outcome: 'feature-spawn',
    expectedRevision: '0123456789abcdef',
    expectedFingerprint: 'a'.repeat(64),
    responses: [{
      decisionId: 'action-density',
      kind: 'single',
      selectedOptionIds: ['compact'],
      comment: 'Keep the header in one compact row.',
    }],
  }));
  expect(captured.decisionBodies[1]).toEqual(expect.objectContaining({
    confirmed: true,
    outcome: 'feature-spawn',
    expectedRevision: '1234567890abcdef',
    expectedFingerprint: 'b'.repeat(64),
    spawnedTaskKeys: ['AGT-2400'],
  }));
  expect(captured.taskBodies[1]).toEqual(expect.objectContaining({
    title: 'Implement Action Bar Dossier',
    watchPath: WATCH_PATH,
    targetState: '1-preparation',
    taskType: 'feature',
  }));
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await capture(page, 'workbench-viewer', `decision-inline-after-${theme}--mocked.png`);
  }
});
