import { expect } from '@playwright/test';
import type { Page, Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { contrastRatio } from '../helpers/contrast';
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

const DOSSIER_HTML = `<main>
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
}

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installMocks(page: Page): Promise<CapturedCalls> {
  const captured: CapturedCalls = { taskBodies: [], decisionBodies: [], steerBodies: [] };
  let taskCreated = false;
  let decision: Record<string, unknown> | null = null;
  let decisionStage: string | null = null;
  let revision = '0123456789abcdef';
  let fingerprint = 'a'.repeat(64);

  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/watch-paths', route => json(route, [{
    name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH,
  }]));
  await page.route('**/api/workspaces**', route => json(route, [{
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
  await page.route('**/api/runner/status**', route => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-08-11T10:00:00Z', snapshots: [], ttlSeconds: 600,
  }));
  await page.route('**/api/cli/usage**', route => json(route, {
    at: '2026-08-11T10:00:00Z', sessions: [],
  }));
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
        status: 'active', phase: 'decision-ready',
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
        status: 'active', phase: 'decision-ready',
        updatedAtUtc: '2026-08-11T10:00:00Z',
        entryPath: 'docs/operations/naming-dossier/index.html',
        valid: true, error: null, sourceTaskKeys: ['AGT-2600'],
      }],
    }),
  );
  await page.route('**/api/tasks/archive**', route => json(route, {
    items: [], total: 0, offset: 0, limit: 50,
  }));
  await page.route('**/api/tasks/grouped**', route => json(route, taskCreated ? {
    ...EMPTY_GROUPED,
    ready: [{
      id: 'naming-feature-1', key: 'AGT-2611', displayKey: 'AGT-2611',
      taskKey: `${PROJECT}::AGT-2611`, title: 'Implement the stable naming contract',
      state: '2-ready', projectName: PROJECT, watchPath: WATCH_PATH,
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
  await page.route(/\/api\/tasks(?:\?.*)?$/, route => {
    if (route.request().method() !== 'POST') return json(route, []);
    const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;
    captured.taskBodies.push(body);
    taskCreated = true;
    return json(route, { id: 'naming-feature-1' });
  });
  await page.route(/\/api\/tasks\/naming-feature-1(?:\?.*)?$/, route => json(route, {
    info: {
      id: 'naming-feature-1', key: 'AGT-2611', displayKey: 'AGT-2611',
      taskKey: `${PROJECT}::AGT-2611`, title: 'Implement the stable naming contract',
      state: '2-ready', projectName: PROJECT, watchPath: WATCH_PATH,
    },
  }));
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
    `**/api/orchestrator/sessions/workbench:${encodeURIComponent(PROJECT)}/${WORKBENCH_KEY}/turns`,
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
        status: decisionStage === 'succeeded' ? 'decided' : 'active',
        phase: 'decision-ready', updatedAtUtc: '2026-08-11T10:00:00Z',
        entryPath: 'docs/operations/naming-dossier/index.html', valid: true, error: null,
        sourceTaskKeys: ['AGT-2600'], relatedTaskKeys: taskCreated ? ['AGT-2611'] : [],
        lifecycleState: decisionStage === 'succeeded' ? 'decided' : 'review-requested',
        decision, decisionStage,
      },
      html: DOSSIER_HTML, branch: 'develop', revision,
      workingTreeModified: false, fingerprint, entryFingerprint: 'entry-a',
    }),
  );

  return captured;
}

async function seedWorkbench(page: Page) {
  await page.addInitScript(({ project, workbenchId }) => {
    if (!sessionStorage.getItem('agt2610-workbench-seeded')) {
      sessionStorage.clear();
      sessionStorage.setItem('agt2610-workbench-seeded', '1');
    }
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'workbench', projectName: project, workbenchId, title: 'Naming Dossier' }],
      activeKey: `workbench:${project}:${workbenchId}`,
    }));
    localStorage.setItem('atp.studio.theme', 'light');
    localStorage.setItem('defaultCliType', 'claude');
    localStorage.setItem('defaultModel:claude', 'claude-opus-4-6');
  }, { project: PROJECT, workbenchId: WORKBENCH_ID });
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

  await inline.getByTestId('workbench-decision-confirm').click();
  const created = inline.getByTestId('workbench-decision-created-tasks');
  await expect(created).toContainText('AGT-2611');
  await expect(created).toContainText('Implement the stable naming contract');
  await expect(created).toContainText('Ready');
  await page.screenshot({
    path: resolve(RESULTS, 'dossier-inline-decision-created-receipt--mocked.png'),
    fullPage: true,
  });

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
