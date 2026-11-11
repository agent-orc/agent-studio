import { expect } from '@playwright/test';
import type { Page, Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { contrastRatio } from '../helpers/contrast';
import { setTheme } from '../helpers/theme';
import { test } from '../fixtures/dev-backend';

const PROJECT = 'Naming Evidence';
const WATCH_PATH = 'C:/evidence/naming';
const WORKBENCH_ID = 'naming-dossier';
const WORKBENCH_KEY = 'AGT-W33';
const RESULTS = resolve(
  process.env['JOB_RESULTS_DIR'] ?? resolve(__dirname, '..', '..', '..', 'results', 'AGT-2768'),
);

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
  escalated: [], review: [], completed: [], archive: [],
};

const DOSSIER_HTML = `<style>
  :root { color-scheme: light dark; --bg: #ffffff; --fg: #171717; --field: #ffffff; }
  @media (prefers-color-scheme: dark) {
    :root { --bg: #1a1a19; --fg: #f7f6f2; --field: #252523; }
  }
  body { background: var(--bg); color: var(--fg); }
  textarea { background: var(--field); color: var(--fg); }
</style><main>
  <h1>Naming Dossier</h1>
  <p>Select the stable public naming contract.</p>
  <section data-decision-id="public-name" data-decision-kind="single">
    <h2>Choose the public name</h2>
    <ul>
      <li data-option-id="option-a">A · Same grid</li>
      <li data-option-id="option-b">B · Dedicated header</li>
    </ul>
    <label>Reason <textarea data-comment="Naming reason"></textarea></label>
  </section>
</main>`;

interface CapturedCalls {
  taskBodies: Record<string, unknown>[];
  decisionBodies: Record<string, unknown>[];
  steerBodies: Record<string, unknown>[];
  deletedTaskIds: string[];
}

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

interface DescriptorState {
  status: string;
  phase: string;
  lifecycleState: string;
}

const DECISION_READY: DescriptorState = {
  status: 'active', phase: 'decision-ready', lifecycleState: 'review-requested',
};

async function installMocks(
  page: Page, descriptor: DescriptorState = DECISION_READY,
): Promise<CapturedCalls> {
  const captured: CapturedCalls = { taskBodies: [], decisionBodies: [], steerBodies: [], deletedTaskIds: [] };
  let taskCreated = false;
  let decision: Record<string, unknown> | null = null;
  let decisionStage: string | null = null;
  let revision = '0123456789abcdef';
  let fingerprint = 'a'.repeat(64);

  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/watch-paths', route => json(route, [{
    name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH,
  }]));
  await page.route('**/api/workspaces/*/quota/admission', route => json(route, {
    workspaceId: 'workspace-evidence',
    workspace: {
      projectId: null, project: null, status: 'ok', message: null,
      overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false,
    },
    projects: [],
  }));
  await page.route('**/api/v1/workspaces**', route => json(route, [{
    id: 'workspace-evidence', displayName: 'Evidence', sortOrder: 0, isDefault: true,
    projects: [{
      id: 'project-naming', displayName: PROJECT, shortCode: 'NDS',
      workspaceId: 'workspace-evidence', storageLocation: WATCH_PATH,
      sortOrder: 0, archived: false, urls: [],
    }],
  }]));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/v1/studio/runner/status**', route => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-08-11T10:00:00Z', snapshots: [], ttlSeconds: 600,
  }));
  await page.route('**/api/cli/usage**', route => json(route, {
    at: '2026-08-11T10:00:00Z', sessions: [],
  }));
  await page.route('**/api/usage/cockpit**', route => json(route, {
    workspaceId: 'workspace-evidence', clis: [], cost: null,
  }));
  await page.route('**/api/token-pricing/calculate', route => json(route, { items: [] }));
  await page.route(/\/api\/cli\/[^/]+\/models(?:\?.*)?$/, route => {
    const cli = new URL(route.request().url()).pathname.split('/')[3];
    return json(route, {
      models: cli === 'codex' ? [{
        id: 'gpt-5.6-sol', label: 'GPT-5.6-Sol', multiplier: null, vendor: 'openai',
        isDefault: true, thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'medium',
      }] : [{
        id: 'claude-opus-4-6', label: 'Claude Opus 4.6', multiplier: null,
        vendor: 'anthropic', isDefault: true, thinkingLevels: [], defaultThinkingLevel: null,
      }],
      source: 'workbench-details-evidence',
    });
  });
  await page.route('**/api/cli/maintenance-model', route => json(route, {
    cliType: 'codex', model: 'gpt-5', thinkingLevel: null,
  }));
  await page.route('**/api/cli/model-routing/recommendation**', route => json(route, {
    model: 'gpt-5', thinkingLevel: null, tier: 'complex', taskType: 'feature',
    economyDowngraded: false, policyVersion: 'e2e',
    policyWikiPath: 'docs/system/domains/model-routing-policy.md',
  }));
  await page.route('**/api/crash-recovery/pending**', route => json(route, { pending: [] }));
  await page.route('**/api/workbenches**', route => json(route, {
    includesHistory: true,
    count: 1,
    items: [{
      projectName: PROJECT,
      workbench: {
        id: WORKBENCH_ID, key: WORKBENCH_KEY, title: 'Naming Dossier',
        summary: 'Choose the stable naming contract used by public references.',
        status: descriptor.status, phase: descriptor.phase,
        updatedAtUtc: '2026-08-11T10:00:00Z',
        entryPath: 'docs/operations/naming-dossier/index.html',
        valid: true, error: null, sourceTaskKeys: ['AGT-2600'],
      },
    }],
  }));
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches**`,
    route => json(route, {
      projectName: PROJECT,
      includesHistory: false,
      count: 1,
      items: [{
        id: WORKBENCH_ID, key: WORKBENCH_KEY, title: 'Naming Dossier',
        summary: 'Choose the stable naming contract used by public references.',
        status: descriptor.status, phase: descriptor.phase,
        updatedAtUtc: '2026-08-11T10:00:00Z',
        entryPath: 'docs/operations/naming-dossier/index.html',
        valid: true, error: null, sourceTaskKeys: ['AGT-2600'],
      }],
    }),
  );
  await page.route('**/api/tasks/archive**', route => json(route, {
    items: [], total: 0, offset: 0, limit: 50,
  }));
  await page.route('**/api/v1/studio/board**', route => json(route, taskCreated ? {
    ...EMPTY_GROUPED,
    ready: [{
      id: 'naming-feature-1', key: 'AGT-2611', displayKey: 'AGT-2611',
      taskKey: `${PROJECT}::AGT-2611`, title: 'Implement the stable naming contract',
      state: '2-ready', projectName: PROJECT, watchPath: WATCH_PATH,
      folderPath: `${WATCH_PATH}/tasks/naming-feature-1`, order: 0,
      agent: 'codex', cliType: 'codex', log: [], commits: [],
    }],
  } : EMPTY_GROUPED));
  await page.route('**/api/tasks/reference-status', route => json(route, {
    items: taskCreated ? [{
      key: 'AGT-2611', exists: true, taskKey: `${PROJECT}::AGT-2611`,
      title: 'Implement the stable naming contract', lane: '2-ready',
      projectId: 'project-naming', projectName: PROJECT, projectColor: null,
      merge: null, reviewGrade: null,
    }] : [],
  }));
  await page.route('**/api/tasks/naming-feature-1/core**', route => json(route, {
    state: 'ready', projectId: 'project-naming', projectName: PROJECT,
    id: 'naming-feature-1', taskKey: `${PROJECT}::AGT-2611`,
    watchPath: WATCH_PATH, folderPath: `${WATCH_PATH}/tasks/naming-feature-1`,
    key: 'AGT-2611', title: 'Implement the stable naming contract',
    kind: 'task', taskType: 'feature', lane: '2-ready',
    enteredLaneAt: '2026-08-11T10:00:00Z', order: 0, mode: 'coding',
    released: false, pendingIntent: false,
    pins: { modelExplicit: false, thinkingLevelExplicit: false, allowWebAccess: false, noBranchExpected: false },
    actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
    blocking: { dependencyBlocked: false, dependencyState: 'ready', dependencies: [], dependsOn: [], blockedBy: [] },
    runtime: { location: 'none', leaseState: 'none' }, runtimeVersion: 'r0',
    statusSummary: { state: 'missing', originalBytes: 0 },
    prompt: { state: 'ready', text: 'Implement the stable naming contract.', originalBytes: 37 },
    timeline: { state: 'missing', events: [], originalBytes: 0 }, coreVersion: '1',
  }));
  await page.route('**/api/tasks/naming-feature-1/details/*', route => {
    const resource = new URL(route.request().url()).pathname.split('/').pop();
    const data: Record<string, unknown> = {
      overview: {},
      documents: { name: 'status', markdown: null, summaryState: null },
      usage: { tokenSummary: null, lastUsage: null, contextUsage: null },
      git: { mergeSignal: null, integration: null, publishSignal: null, testEvidence: null, commit: null, commits: [] },
      review: { reviewProjection: null, evidence: null },
      history: { promptHistory: [], titleHistory: [], log: [] },
    };
    return json(route, {
      id: 'naming-feature-1', taskKey: `${PROJECT}::AGT-2611`,
      projectId: 'project-naming', attemptId: null, coreVersion: '1',
      resource, version: `${resource}-1`, computedAt: null, state: 'ready',
      data: data[resource ?? ''] ?? null, reason: null,
    });
  });
  await page.route('**/api/tasks/naming-feature-1/pipeline**', route => json(route, {
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null, cost: null, config: {},
  }));
  await page.route('**/api/tasks/naming-feature-1/session-events**', route => json(route, {
    events: [], sessionChain: [],
  }));
  await page.route('**/api/tasks/naming-feature-1/runs**', route => json(route, {
    runs: [], runnerEvents: [],
  }));
  await page.route(/\/api\/tasks(?:\?.*)?$/, route => {
    if (route.request().method() !== 'POST') return json(route, []);
    const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;
    captured.taskBodies.push(body);
    taskCreated = true;
    return json(route, { id: 'naming-feature-1' });
  });
  await page.route(/\/api\/v1\/projects\/-\/tasks\/naming-feature-1(?:\?.*)?$/, route => {
    if (route.request().method() !== 'DELETE') return json(route, {});
    captured.deletedTaskIds.push('naming-feature-1');
    taskCreated = false;
    return json(route, {});
  });
  await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/naming-feature-1(?:\?.*)?$/, route => {
    if (route.request().method() !== 'GET') return route.fallback();
    return json(route, {
      info: {
        id: 'naming-feature-1', key: 'AGT-2611', displayKey: 'AGT-2611',
        taskKey: `${PROJECT}::AGT-2611`, title: 'Implement the stable naming contract',
        state: '2-ready', projectName: PROJECT, watchPath: WATCH_PATH,
        folderPath: `${WATCH_PATH}/tasks/naming-feature-1`, order: 0,
        agent: 'codex', cliType: 'codex', commits: [],
      },
      promptMarkdown: 'Implement the stable naming contract.',
      statusMarkdown: '', log: [], promptHistory: [], reviewEvidence: [],
    });
  });
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_KEY}/references`,
    route => json(route, {
      projectName: PROJECT, workbenchKey: WORKBENCH_KEY, workbenchId: WORKBENCH_ID,
      legacyTaskKeys: [], items: [],
    }),
  );
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}/decisions/prepare`,
    route => {
      const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;
      captured.decisionBodies.push(body);
      revision = '1234567890abcdef';
      fingerprint = 'b'.repeat(64);
      decisionStage = 'prepared';
      return json(route, {
        success: true, errorCode: null, error: null, workbenchId: WORKBENCH_ID,
        operationId: body['operationId'], outcome: body['outcome'], decisionStage,
        revision, fingerprint, spawnedTaskKeys: [], responses: body['responses'],
        taskDraft: body['task'], idempotent: false,
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
      const outcome = body['outcome'] as string;
      decisionStage = outcome === 'rework' ? 'pending' : 'succeeded';
      decision = {
        action: outcome === 'rework' ? 'rework-requested' : 'created-card',
        outcome, state: outcome === 'rework' ? 'pending' : 'succeeded', operationId: body['operationId'],
        sourceRevision: body['expectedRevision'], sourceFingerprint: body['expectedFingerprint'],
        sourceEntryFingerprint: 'entry-a',
        preparedAt: '2026-08-11T10:01:00Z', preparedBy: body['actor'],
        confirmedAt: '2026-08-11T10:02:00Z', confirmedBy: body['actor'],
        decidedAt: outcome === 'rework' ? null : '2026-08-11T10:02:00Z', reason: null, failure: null,
        spawnedTaskKeys: body['spawnedTaskKeys'], responses: body['responses'],
        taskDraft: outcome === 'rework' ? null : body['task'], cliType: body['cliType'],
        model: body['model'], thinkingLevel: body['thinkingLevel'],
      };
      return json(route, {
        success: true, errorCode: null, error: null, workbenchId: WORKBENCH_ID,
        operationId: body['operationId'], outcome, decisionStage,
        revision, fingerprint, spawnedTaskKeys: body['spawnedTaskKeys'],
        responses: body['responses'], taskDraft: body['task'], idempotent: false,
      });
    },
  );
  await page.route(
    `**/api/v1/studio/orchestrator/sessions/workbench:${encodeURIComponent(PROJECT)}/${WORKBENCH_KEY}/turns`,
    route => {
      captured.steerBodies.push(JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>);
      return json(route, { status: 'queued', contextKey: `workbench:${PROJECT}/${WORKBENCH_KEY}` });
    },
  );
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}`,
    route => json(route, {
      workbench: {
        id: WORKBENCH_ID, key: WORKBENCH_KEY, title: 'Naming Dossier',
        summary: 'Choose the stable naming contract used by public references.',
        status: decisionStage === 'succeeded' ? 'decided' : descriptor.status,
        phase: descriptor.phase, updatedAtUtc: '2026-08-11T10:00:00Z',
        entryPath: 'docs/operations/naming-dossier/index.html', valid: true, error: null,
        sourceTaskKeys: ['AGT-2600'], relatedTaskKeys: taskCreated ? ['AGT-2611'] : [],
        lifecycleState: decisionStage === 'succeeded' ? 'decided' : descriptor.lifecycleState,
        decision, decisionStage,
      },
      html: DOSSIER_HTML, branch: 'develop', revision,
      workingTreeModified: false, fingerprint, entryFingerprint: 'entry-a',
    }),
  );

  return captured;
}

async function seedWorkbench(page: Page, theme: 'light' | 'dark' = 'light') {
  await page.addInitScript(({ project, workbenchId, theme }) => {
    if (!sessionStorage.getItem('agt2610-workbench-seeded')) {
      sessionStorage.clear();
      sessionStorage.setItem('agt2610-workbench-seeded', '1');
    }
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'workbench', projectName: project, workbenchId, title: 'Naming Dossier' }],
      activeKey: `workbench:${project}:${workbenchId}`,
    }));
    localStorage.setItem('atp.studio.theme', theme);
    localStorage.setItem('defaultCliType', 'claude');
    localStorage.setItem('defaultModel:claude', 'claude-opus-4-6');
  }, { project: PROJECT, workbenchId: WORKBENCH_ID, theme });
}

test('inline Dossier decision starts a Ready task with the selected agent', async ({ page, devBackend }) => {
  expect(devBackend.port).toBeGreaterThan(0);
  mkdirSync(RESULTS, { recursive: true });
  await page.setViewportSize({ width: 1600, height: 1000 });
  const captured = await installMocks(page);
  await seedWorkbench(page);
  await page.goto('/');
  await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });

  const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  const stableOption = frame.locator('[data-option-id="option-a"] input');
  await expect(stableOption).toBeVisible();
  await stableOption.check();
  await frame.locator('[data-studio-decision-comment]')
    .fill('Total soll ein bisschen weiter nach oben abgesetzt werden');
  const inline = page.getByTestId('workbench-inline-decision-action');
  await expect(inline.getByTestId('workbench-decision-start')).toBeEnabled();
  await inline.getByTestId('workbench-decision-start').press('Enter');
  await expect(inline.getByTestId('workbench-decision-goal'))
    .toHaveValue('Implement Naming Dossier according to the source Dossier and its recorded decisions.');
  const seededPrompt = await inline.getByTestId('workbench-decision-prompt-preview').innerText();
  expect(seededPrompt.match(/Recorded decisions/g)).toHaveLength(1);
  await expect(inline.getByTestId('workbench-decision-prompt-preview'))
    .toContainText('A · Same grid');
  await expect(inline.getByTestId('workbench-decision-prompt-preview'))
    .toContainText('Total soll ein bisschen weiter nach oben abgesetzt werden');
  await inline.getByTestId('workbench-decision-title').fill('Implement the stable naming contract');
  await inline.getByTestId('workbench-decision-goal').fill(
    'Apply the selected stable key across public references and navigation.',
  );

  await inline.getByTestId('workbench-decision-agent').click();
  await page.getByTestId('workbench-decision-agent-picker-cli-codex').click();
  await page.getByTestId('workbench-decision-agent-picker-model-gpt-5.6-sol').click();
  await page.getByTestId('workbench-decision-agent-picker-thinking-high').click();
  await page.getByTestId('workbench-decision-agent-picker-done').click();
  await expect(inline.getByTestId('workbench-decision-agent'))
    .toHaveAttribute('aria-label', 'Model: Codex · gpt-5.6-sol · high');

  const colours = await inline.getByTestId('workbench-decision-title').evaluate(element => {
    const style = getComputedStyle(element);
    return { color: style.color, background: style.backgroundColor };
  });
  expect(contrastRatio(colours.color, colours.background), 'title field contrast')
    .toBeGreaterThanOrEqual(4.5);
  await page.screenshot({
    path: resolve(RESULTS, 'dossier-inline-decision-confirmation--mocked.png'),
    fullPage: true,
  });

  let releaseCreate!: () => void;
  const waitForCapture = new Promise<void>(resolveCapture => { releaseCreate = resolveCapture; });
  await page.route(/\/decisions\/prepare(?:\?.*)?$/, async route => {
    if (route.request().method() === 'POST') await waitForCapture;
    await route.fallback();
  });
  await inline.getByTestId('workbench-decision-confirm').click();
  const pendingButton = inline.getByTestId('workbench-decision-confirm');
  await expect(pendingButton).toHaveAttribute('aria-busy', 'true');
  const pendingStyle = await pendingButton.evaluate(button => ({
    fontSize: getComputedStyle(button).fontSize,
    label: getComputedStyle(button, '::after').content,
  }));
  expect(pendingStyle).toEqual({ fontSize: '0px', label: '"Starting…"' });
  for (const variant of [
    { name: 'light', theme: 'light', width: 1600 },
    { name: 'dark', theme: 'dark', width: 1600 },
    { name: '390', theme: 'light', width: 390 },
  ] as const) {
    await page.setViewportSize({ width: variant.width, height: 1000 });
    await setTheme(page, variant.theme);
    await page.screenshot({ path: resolve(RESULTS, `dossier-pending-${variant.name}--mocked.png`) });
  }
  releaseCreate();
  const created = inline.getByTestId('workbench-decision-created-tasks');
  await expect(created).toContainText('AGT-2611');
  await expect(created).toContainText('Implement the stable naming contract');
  await expect(created).toContainText('Ready');
  await expect(inline.getByTestId('workbench-decision-receipt')).toContainText('AGT-2611');
  await expect(page.getByTestId('workbench-viewer-status')).toHaveAttribute('data-status', 'decided');
  await page.screenshot({
    path: resolve(RESULTS, 'dossier-inline-decision-created-receipt--mocked.png'),
    fullPage: true,
  });
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'dark'; });
  await page.screenshot({
    path: resolve(RESULTS, 'dossier-inline-decision-created-receipt-dark--mocked.png'),
    fullPage: true,
  });
  await page.emulateMedia({ colorScheme: 'light' });
  await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'light'; });
  await page.setViewportSize({ width: 390, height: 844 });
  const cardLink = await created.locator('a').first().boundingBox();
  expect(cardLink).not.toBeNull();
  expect(cardLink!.x + cardLink!.width).toBeLessThanOrEqual(390);
  await page.screenshot({
    path: resolve(RESULTS, 'dossier-inline-decision-created-receipt-mobile--mocked.png'),
    fullPage: true,
  });

  await page.setViewportSize({ width: 1600, height: 1000 });
  const runtimeErrors: string[] = [];
  page.on('console', message => {
    if (message.type() === 'error' && message.text().includes('TypeError'))
      runtimeErrors.push(message.text());
  });
  await page.goto(`/?job=naming-feature-1&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  const overview = page.getByTestId('overview-status');
  await expect(overview).toBeVisible();
  await expect(page.getByTestId('overview-title'))
    .toContainText('Implement the stable naming contract');
  await page.waitForTimeout(1000);
  const runtimeError = page.getByRole('alertdialog');
  if (await runtimeError.isVisible({ timeout: 500 }).catch(() => false))
    throw new Error(`Spawned card runtime error: ${await runtimeError.innerText()}`);
  for (const label of ['Last Activity', 'Created', 'Owner'])
    await expect(overview.getByText(label, { exact: true })).toHaveCount(0);
  await expect(page.getByText(/^Attempt \d+/)).toHaveCount(0);
  for (const variant of [
    { width: 1600, theme: 'light', name: 'light' },
    { width: 1600, theme: 'dark', name: 'dark' },
    { width: 390, theme: 'light', name: '390-light' },
    { width: 390, theme: 'dark', name: '390-dark' },
  ] as const) {
    await page.setViewportSize({ width: variant.width, height: 1000 });
    await setTheme(page, variant.theme);
    await page.waitForTimeout(500);
    if (await runtimeError.isVisible().catch(() => false))
      throw new Error(`Spawned card runtime error after theme change: ${await runtimeError.innerText()}`);
    await page.screenshot({ path: resolve(RESULTS, `agt-3666-spawned-card-${variant.name}--mocked.png`) });
  }
  expect(runtimeErrors).toEqual([]);

  expect(captured.decisionBodies).toHaveLength(2);
  expect(captured.taskBodies).toEqual([expect.objectContaining({
    title: 'Implement the stable naming contract',
    watchPath: WATCH_PATH,
    targetState: '2-ready',
    cliType: 'codex',
    model: 'gpt-5.6-sol',
    thinkingLevel: 'high',
    taskType: 'feature',
  })]);
  expect(captured.decisionBodies[0]).toEqual(expect.objectContaining({
    outcome: 'feature-spawn',
    expectedRevision: '0123456789abcdef',
    expectedFingerprint: 'a'.repeat(64),
    task: expect.objectContaining({
      title: 'Implement the stable naming contract',
      goal: 'Apply the selected stable key across public references and navigation.',
    }),
    responses: [expect.objectContaining({
      selectedOptionIds: ['option-a'],
      comment: 'Total soll ein bisschen weiter nach oben abgesetzt werden',
    })],
  }));
  expect(captured.decisionBodies[1]).toEqual(expect.objectContaining({
    confirmed: true,
    expectedRevision: '1234567890abcdef',
    expectedFingerprint: 'b'.repeat(64),
    spawnedTaskKeys: ['AGT-2611'],
    cliType: 'codex',
    model: 'gpt-5.6-sol',
    thinkingLevel: 'high',
  }));
});

test('failed Dossier confirmation removes its new task and explains the outcome', async ({ page, devBackend }) => {
  expect(devBackend.port).toBeGreaterThan(0);
  const captured = await installMocks(page);
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}/decisions/confirm`,
    route => json(route, {
      success: false, errorCode: 'write-failed',
      error: 'Managed repository commit failed: Author identity unknown. Run git config --global user.email',
    }, 500),
  );
  await seedWorkbench(page);
  await page.goto('/');
  const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  await frame.locator('[data-option-id="option-a"] input').check();
  await frame.locator('[data-studio-decision-host-slot]').scrollIntoViewIfNeeded();
  const inline = page.getByTestId('workbench-inline-decision-action');
  await expect.poll(() => inline.evaluate(element => getComputedStyle(element).pointerEvents))
    .toBe('auto');
  await inline.getByTestId('workbench-decision-start').click();
  await inline.getByTestId('workbench-decision-confirm').click();
  await expect.poll(() => captured.deletedTaskIds).toEqual(['naming-feature-1']);
  await expect(inline.getByRole('alert')).toContainText('The new task was removed');
  await expect(inline.getByRole('alert')).not.toContainText('git config');
  const board = await page.evaluate(async () =>
    (await fetch('/api/v1/studio/board')).json() as Promise<{ ready: unknown[] }>);
  expect(board.ready).toHaveLength(0);
});

test('inline Dossier decision requests rework through its orchestrator session', async ({ page, devBackend }) => {
  expect(devBackend.port).toBeGreaterThan(0);
  mkdirSync(RESULTS, { recursive: true });
  await page.setViewportSize({ width: 1600, height: 1000 });
  const captured = await installMocks(page);
  await seedWorkbench(page);
  await page.goto('/');
  await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });

  const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  await frame.locator('[data-studio-decision-comment]')
    .fill('Raise the total and revise the supporting rationale.');
  const inline = page.getByTestId('workbench-inline-decision-action');
  await expect(inline.getByTestId('workbench-decision-start')).toBeDisabled();
  await inline.getByTestId('workbench-decision-rework').click();
  await inline.getByTestId('workbench-decision-agent').click();
  await page.getByTestId('workbench-decision-agent-picker-cli-codex').click();
  await page.getByTestId('workbench-decision-agent-picker-model-gpt-5.6-sol').click();
  await page.getByTestId('workbench-decision-agent-picker-thinking-high').click();
  await page.getByTestId('workbench-decision-agent-picker-done').click();
  await inline.getByTestId('workbench-decision-confirm-rework').click();

  await expect(inline.getByTestId('workbench-decision-receipt'))
    .toContainText('Rework requested on');
  await expect(inline.getByTestId('workbench-decision-receipt'))
    .toContainText('gpt-5.6-sol');
  expect(captured.steerBodies).toHaveLength(1);
  expect(captured.steerBodies[0]).toEqual(expect.objectContaining({
    cliType: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'high',
  }));
  expect(captured.steerBodies[0]['prompt']).toContain(
    'Raise the total and revise the supporting rationale.',
  );
  await page.screenshot({
    path: resolve(RESULTS, 'dossier-inline-decision-rework--mocked.png'),
    fullPage: true,
  });
});

// AGT-3697: a concept delivery publishes status decision-pending and leaves
// phase at shaping. The Studio must accept that descriptor without a manual
// phase edit.
for (const variant of [
  { name: 'light', theme: 'light' as const, width: 1600, height: 1000 },
  { name: 'dark', theme: 'dark' as const, width: 1600, height: 1000 },
  { name: '390', theme: 'light' as const, width: 390, height: 844 },
]) {
  test(`decision-pending concept Dossier in shaping is decidable (${variant.name})`, async ({ page, devBackend }) => {
    expect(devBackend.port).toBeGreaterThan(0);
    mkdirSync(RESULTS, { recursive: true });
    await page.setViewportSize({ width: variant.width, height: variant.height });
    await installMocks(page, {
      status: 'decision-pending', phase: 'shaping', lifecycleState: 'review-requested',
    });
    await seedWorkbench(page, variant.theme);
    await page.goto('/');
    await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });

    const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
    await frame.locator('[data-option-id="option-a"] input').check();
    const inline = page.getByTestId('workbench-inline-decision-action');
    await expect(inline.getByTestId('workbench-decision-start')).toBeEnabled();
    await expect(page.getByTestId('workbench-decision-gate')).toHaveCount(0);
    await page.screenshot({
      path: resolve(RESULTS, `agt-3697-decision-pending-shaping-decidable--${variant.name}.png`),
    });
  });
}

test('active shaping Dossier names the descriptor states that open decisions', async ({ page, devBackend }) => {
  expect(devBackend.port).toBeGreaterThan(0);
  mkdirSync(RESULTS, { recursive: true });
  await page.setViewportSize({ width: 1600, height: 1000 });
  await installMocks(page, { status: 'active', phase: 'shaping', lifecycleState: 'in-progress' });
  await seedWorkbench(page);
  await page.goto('/');
  await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });

  const gate = page.getByTestId('workbench-decision-gate').first();
  await expect(gate).toContainText('status decision-pending or phase decision-ready');
  await expect(page.getByTestId('workbench-decision-start')).toHaveCount(0);
  await gate.scrollIntoViewIfNeeded();
  await page.screenshot({
    path: resolve(RESULTS, 'agt-3697-active-shaping-gate-notice--light.png'),
  });
});
