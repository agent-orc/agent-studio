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
const DOSSIER_PATH = 'docs/operations/naming-dossier/index.html';
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

/** The revision the follow-up round delivers: new section, authored default on option A. */
const REVISED_HTML = `<main>
  <h1>Naming Dossier</h1>
  <p>Select the stable public naming contract.</p>
  <section data-decision-id="public-name" data-decision-kind="single">
    <h2>Choose the public name</h2>
    <ul>
      <li data-option-id="option-a"><input type="radio" checked> A · Same grid (recommended)</li>
      <li data-option-id="option-b"><input type="radio"> B · Dedicated header</li>
    </ul>
    <label>Reason <textarea data-comment="Naming reason"></textarea></label>
  </section>
  <section><h2>Privacy</h2><p>Public names never carry personal data.</p></section>
</main>`;

const REVISION_CARD = {
  available: true, key: 'AGT-2600', jobId: 'naming-concept', title: 'Concept: naming contract',
  lane: '6-completed', mode: 'concept', reason: null,
};

interface CapturedCalls {
  taskBodies: Record<string, unknown>[];
  decisionBodies: Record<string, unknown>[];
  steerBodies: Record<string, unknown>[];
  deletedTaskIds: string[];
  landRevision: () => void;
  settleUnchanged: () => void;
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

function cardInfo(spawned: boolean) {
  const id = spawned ? 'naming-feature-1' : 'naming-concept';
  const key = spawned ? 'AGT-2611' : 'AGT-2600';
  const state = spawned ? '2-ready' : '5-human-review';
  return {
    id, key, displayKey: key, taskKey: `${PROJECT}::${id}`,
    title: spawned ? 'Implement the stable naming contract' : 'Naming concept',
    state, projectName: PROJECT, watchPath: WATCH_PATH,
    folderPath: `${WATCH_PATH}/${state}/${id}`,
    mode: spawned ? 'coding' : 'concept',
    dossier: { path: DOSSIER_PATH },
    conceptDossier: spawned ? null : { repoRelativePath: DOSSIER_PATH, contractSatisfied: true,
      noDossierNeeded: false },
    requiresIntegration: false, integration: { status: 'not-applicable' },
    order: 1, agent: 'codex', cliType: 'codex', model: 'gpt-5.6-sol',
    createdAt: spawned ? null : '2026-08-11T10:00:00Z',
    lastActivity: spawned ? null : '2026-08-11T10:00:00Z',
    sessionName: null, useOwnSession: null, lastUsage: null,
    execution: null, commit: null, commits: [], ownerClientId: spawned ? null : 'local-default',
  };
}

function cardDetail(spawned: boolean) {
  return {
    info: cardInfo(spawned), promptMarkdown: '# Naming', statusMarkdown: '# Status',
    log: [], promptHistory: [], contextUsage: null, reviewEvidence: [], summaryState: null,
  };
}

function cardCore(spawned: boolean) {
  const card = cardInfo(spawned);
  return {
    state: 'ready', projectId: 'project-naming', projectName: PROJECT,
    id: card.id, taskKey: card.taskKey, key: card.key,
    watchPath: WATCH_PATH, folderPath: card.folderPath, title: card.title,
    kind: 'task', taskType: spawned ? 'feature' : 'chore', lane: card.state,
    archiveState: null, enteredLaneAt: card.createdAt, order: 1, mode: card.mode,
    released: false, pendingIntent: false, coreVersion: '7',
    pins: { model: card.model, modelExplicit: true, thinkingLevel: null,
      thinkingLevelExplicit: false, cliType: card.cliType, contextMode: null,
      useOwnSession: null, allowWebAccess: false, noBranchExpected: false },
    actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
    blocking: { blockerType: null, blockerCondition: null, blockerStatus: null,
      blockerDescription: null, needsInput: null, dependencyBlocked: false,
      dependencyState: 'ready', dependencies: [] },
    runtime: { attemptId: null, runnerId: null, runnerName: null, hostname: null,
      executionStatus: null, location: 'none', heartbeatAt: null,
      leaseState: 'none', leaseId: null },
    runtimeVersion: 'v1',
    statusSummary: { state: 'ready', text: 'Ready', originalBytes: 5,
      hash: 'status', cursor: null },
    prompt: { state: 'ready', text: '# Naming', originalBytes: 8,
      hash: 'prompt', cursor: null, continuationUrl: null },
    timeline: { state: 'ready', events: [], cursor: null, continuationUrl: null },
  };
}

function cardResource(id: string, name: string, data: unknown) {
  return { id, taskKey: `${PROJECT}::${id}`, projectId: 'project-naming',
    attemptId: null, coreVersion: '7', resource: name, version: 'v1',
    computedAt: '2026-08-11T10:00:00Z', state: 'ready', data, reason: null };
}

async function installMocks(
  page: Page, descriptor: DescriptorState = DECISION_READY, failFirstConfirm = false,
): Promise<CapturedCalls> {
  let revisionLanded = false;
  let revisionUnchanged = false;
  const captured: CapturedCalls = {
    taskBodies: [], decisionBodies: [], steerBodies: [], deletedTaskIds: [],
    landRevision: () => { revisionLanded = true; },
    settleUnchanged: () => { revisionUnchanged = true; },
  };
  let taskCreated = false;
  let decision: Record<string, unknown> | null = null;
  let decisionStage: string | null = null;
  let revision = '0123456789abcdef';
  let fingerprint = 'a'.repeat(64);
  let confirmFailed = false;
  const catalogueStatus = () => decisionStage === 'succeeded' ? 'decided' : descriptor.status;
  const catalogueOpenCount = () => decisionStage === 'succeeded' ? 0
    : descriptor.status === 'decision-pending' || descriptor.phase === 'decision-ready'
      || descriptor.lifecycleState === 'review-requested' ? 1 : 0;

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
  // The header quota read is not under test; the catch-all array would crash it.
  await page.route('**/api/workspaces/*/quota/admission**', route => json(route, {
    workspaceId: 'workspace-evidence',
    workspace: { projectId: null, project: null, status: 'ok', message: null,
      overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false },
    projects: [],
  }));
  // Model prices in the picker are not under test; no known price keeps the pills plain.
  await page.route('**/api/token-pricing/calculate', route => json(route, { items: [] }));
  // The usage cockpit is not under test; a failed read keeps it inert.
  await page.route('**/api/usage/cockpit**', route => json(route, {}, 503));
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
  await page.route('**/api/usage/cockpit**', route => json(route, {
    snapshotVersion: 1, workspaceId: 'workspace-evidence', timeZone: 'UTC', weekStart: 1,
    generatedAt: '2026-08-11T10:00:00Z', calendar: { timeZone: 'UTC', weekStart: 1 }, clis: [],
    cost: { currency: 'USD', todayUsd: null, weekUsd: null, projects: [],
      coverage: { status: 'unavailable', observedAt: null, ttlSeconds: null } },
    runs: [], slots: [], sources: {},
  }));
  await page.route('**/api/token-pricing/calculate', route => json(route, { provider: 'TokenEconomy', items: [] }));
  await page.route('**/api/workspaces/*/quota/admission**', route => json(route, {
    workspaceId: 'workspace-evidence',
    workspace: { codingAllowed: true, hostingAllowed: true, limited: false },
    projects: [],
  }));
  await page.route('**/api/workbenches**', route => json(route, {
    includesHistory: true,
    count: 1,
    items: [{
      projectName: PROJECT,
      workbench: {
        id: WORKBENCH_ID, key: WORKBENCH_KEY, title: 'Naming Dossier',
        summary: 'Choose the stable naming contract used by public references.',
        status: catalogueStatus(), phase: descriptor.phase,
        lifecycleState: decisionStage === 'succeeded' ? 'decided' : descriptor.lifecycleState,
        openDecisionCount: catalogueOpenCount(),
        updatedAtUtc: '2026-08-11T10:00:00Z',
        entryPath: DOSSIER_PATH,
        valid: true, error: null, sourceTaskKeys: ['AGT-2600'],
        linkedTaskKeys: taskCreated ? ['AGT-2600', 'AGT-2611'] : ['AGT-2600'],
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
        status: catalogueStatus(), phase: descriptor.phase,
        lifecycleState: decisionStage === 'succeeded' ? 'decided' : descriptor.lifecycleState,
        openDecisionCount: catalogueOpenCount(),
        updatedAtUtc: '2026-08-11T10:00:00Z',
        entryPath: DOSSIER_PATH,
        valid: true, error: null, sourceTaskKeys: ['AGT-2600'],
        linkedTaskKeys: taskCreated ? ['AGT-2600', 'AGT-2611'] : ['AGT-2600'],
      }],
    }),
  );
  await page.route('**/api/tasks/archive**', route => json(route, {
    items: [], total: 0, offset: 0, limit: 50,
  }));
  await page.route('**/api/v1/studio/board**', route => json(route, taskCreated ? {
    ...EMPTY_GROUPED,
    humanReview: [cardInfo(false)], ready: [cardInfo(true)],
  } : { ...EMPTY_GROUPED, humanReview: [cardInfo(false)] }));
  await page.route('**/api/tasks/reference-status', route => json(route, {
    items: [{
      key: 'AGT-2600', exists: true, taskKey: `${PROJECT}::naming-concept`,
      title: 'Naming concept', lane: '5-human-review',
      projectId: 'project-naming', projectName: PROJECT, projectColor: null,
      merge: null, reviewGrade: null,
    }, ...(taskCreated ? [{
      key: 'AGT-2611', exists: true, taskKey: `${PROJECT}::naming-feature-1`,
      title: 'Implement the stable naming contract', lane: '2-ready',
      projectId: 'project-naming', projectName: PROJECT, projectColor: null,
      merge: null, reviewGrade: null,
    }] : [])],
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
    return json(route, cardDetail(true));
  });
  await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/naming-concept(?:\?.*)?$/, route => json(route, cardDetail(false)));
  for (const [id, spawned] of [['naming-concept', false], ['naming-feature-1', true]] as const) {
    await page.route(new RegExp(`/api/tasks/${id}(\\?|$)`), route => json(route, cardDetail(spawned)));
    await page.route(`**/api/tasks/${id}/core**`, route => json(route, cardCore(spawned)));
    await page.route(`**/api/tasks/${id}/runs**`, route => json(route, {
      runCount: 0, firstStartedAt: null, lastActivityAt: null,
      hasActiveRun: false, runs: [], promptEntries: [], refinements: [], runnerEvents: [],
    }));
    await page.route(`**/api/tasks/${id}/session-events**`, route =>
      json(route, { events: [], sessionChain: [] }));
    await page.route(`**/api/tasks/${id}/output**`, route => json(route, []));
    await page.route(`**/api/tasks/${id}/plan**`, route => json(route, {
      hasPlan: false, source: null, snapshotCount: 0,
      activeItemId: null, items: [], unassignedSubActions: [],
    }));
    await page.route(`**/api/tasks/${id}/pipeline**`, route => json(route, {
      pipeline: { id: 'naming-pipeline', displayName: 'Naming pipeline', version: 1,
        pre: [], core: [], post: [], allSteps: [] },
      execution: null, executions: [], config: {}, cost: null,
    }));
    await page.route(`**/api/tasks/${id}/details/overview**`, route =>
      json(route, cardResource(id, 'overview', {})));
    await page.route(`**/api/tasks/${id}/details/documents**`, route => {
      const name = new URL(route.request().url()).searchParams.get('name');
      return json(route, cardResource(id, 'documents', {
        name, markdown: '# Naming', summaryState: null,
      }));
    });
  }
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
      if (failFirstConfirm && !confirmFailed) {
        confirmFailed = true;
        return json(route, { error: 'Decision confirmation failed. Try again.' }, 500);
      }
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
        request: outcome === 'rework' ? body['request'] ?? null : null,
        revisionTaskKey: outcome === 'rework' ? REVISION_CARD.key : null,
      };
      return json(route, {
        success: true, errorCode: null, error: null, workbenchId: WORKBENCH_ID,
        operationId: body['operationId'], outcome, decisionStage,
        revision, fingerprint, spawnedTaskKeys: body['spawnedTaskKeys'],
        responses: body['responses'], taskDraft: body['task'], idempotent: false,
        ...(outcome === 'rework' ? {
          revisionCard: REVISION_CARD,
          revisionRound: {
            status: 'queued', taskKey: REVISION_CARD.key, title: REVISION_CARD.title,
            queueReason: 'project-busy', queuePosition: 1,
            detail: 'Another card holds the project\'s runner slot. The round starts when the slot is free.',
          },
        } : {}),
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
        status: catalogueStatus(), openDecisionCount: catalogueOpenCount(),
        phase: descriptor.phase, updatedAtUtc: '2026-08-11T10:00:00Z',
        entryPath: 'docs/operations/naming-dossier/index.html', valid: true, error: null,
        sourceTaskKeys: ['AGT-2600'], relatedTaskKeys: taskCreated ? ['AGT-2611'] : [],
        lifecycleState: decisionStage === 'succeeded' ? 'decided' : descriptor.lifecycleState,
        decision, decisionStage,
      },
      cards: {
        projectName: PROJECT, dossierKey: WORKBENCH_KEY,
        dossierPath: 'docs/operations/naming-dossier/index.html',
        implementationSlices: taskCreated ? 1 : 0, completedSlices: 0,
        cards: [{ key: 'AGT-2600', id: 'naming-concept', title: 'Naming concept',
          lane: '5-human-review', state: '5-human-review', environments: [],
          section: null, decision: null, implementationIndex: null },
        ...(taskCreated ? [{ key: 'AGT-2611', id: 'naming-feature-1',
          title: 'Implement the stable naming contract', lane: '2-ready', state: '2-ready',
          environments: [], section: null, decision: null, implementationIndex: null }] : [])],
      },
      html: revisionLanded ? REVISED_HTML : DOSSIER_HTML, branch: 'develop', revision,
      workingTreeModified: false, fingerprint,
      entryFingerprint: revisionLanded ? 'entry-b' : 'entry-a',
      revisionCard: REVISION_CARD,
      revisionRound: revisionLanded || revisionUnchanged ? {
        status: revisionLanded ? 'updated' : 'unchanged',
        taskKey: REVISION_CARD.key, title: REVISION_CARD.title,
        queueReason: null, detail: null,
      } : null,
    }),
  );

  return captured;
}

async function seedWorkbench(
  page: Page, theme: 'light' | 'dark' = 'light', storedModel = true,
) {
  await page.addInitScript(({ project, workbenchId, theme, storedModel }) => {
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
    if (storedModel) localStorage.setItem('defaultModel:claude', 'claude-opus-4-6');
    else localStorage.removeItem('defaultModel:claude');
  }, { project: PROJECT, workbenchId: WORKBENCH_ID, theme, storedModel });
}

for (const scenario of [
  { name: 'light', theme: 'light', width: 1600, height: 1000 },
  { name: 'dark', theme: 'dark', width: 1600, height: 1000 },
  { name: '390px', theme: 'light', width: 390, height: 844 },
] as const) {
  test(`failed Dossier confirmation remains reachable by mouse in ${scenario.name}`, async ({ page, devBackend }) => {
    expect(devBackend.port).toBeGreaterThan(0);
    mkdirSync(RESULTS, { recursive: true });
    await page.setViewportSize({ width: scenario.width, height: scenario.height });
    const captured = await installMocks(page, DECISION_READY, true);
    await seedWorkbench(page, scenario.theme);
    await page.goto('/');

    const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
    const option = frame.locator('[data-option-id="option-a"] input');
    await expect(option).toBeVisible({ timeout: 30_000 });
    await option.check();
    const inline = page.getByTestId('workbench-inline-decision-action');
    await inline.getByTestId('workbench-decision-start').click();
    await inline.getByTestId('workbench-decision-title').fill('Implement the stable naming contract');
    await inline.getByTestId('workbench-decision-goal').fill('Use the selected public name.');
    await inline.getByTestId('workbench-decision-confirm').click();
    await expect(inline.getByRole('alert').first()).toContainText('Decision confirmation failed. Try again.');
    await expect(inline.getByRole('alert').first()).toBeInViewport();
    await expect(inline.getByTestId('workbench-decision-confirm')).toBeEnabled();
    await page.screenshot({
      path: resolve(RESULTS, `dossier-confirm-failed-${scenario.name}.png`),
    });

    await page.reload();
    const restored = page.getByTestId('workbench-inline-decision-action');
    await expect(restored.getByTestId('workbench-decision-title'))
      .toHaveValue('Implement the stable naming contract');
    await expect(restored.getByTestId('workbench-decision-confirm')).toBeEnabled();
    await page.screenshot({
      path: resolve(RESULTS, `dossier-confirm-restored-${scenario.name}.png`),
    });

    if (scenario.name === 'light') {
      await page.getByTestId(`studio-tab-close-workbench:${PROJECT}:${WORKBENCH_ID}`).click();
      await page.getByTestId('studio-ab-workbenches').click();
      await page.getByRole('button', { name: 'Open full view' }).click();
      await expect(page.getByTestId('workbench-viewer')).toBeVisible();
      await expect(page.getByTestId('workbench-inline-decision-action')
        .getByTestId('workbench-decision-confirm')).toBeEnabled();
    }

    await page.getByTestId('workbench-inline-decision-action')
      .getByTestId('workbench-decision-confirm').click();
    await expect(page.getByTestId('workbench-inline-decision-action')
      .getByTestId('workbench-decision-created-tasks')).toContainText('AGT-2611');
    expect(captured.deletedTaskIds).toEqual([]);
    expect(captured.taskBodies).toHaveLength(1);
    expect(captured.decisionBodies).toHaveLength(4);
    await page.screenshot({
      path: resolve(RESULTS, `dossier-confirm-retried-${scenario.name}.png`),
    });
  });
}

test('inline Dossier decision starts a Ready task with the selected agent', async ({ page, devBackend }) => {
  expect(devBackend.port).toBeGreaterThan(0);
  mkdirSync(RESULTS, { recursive: true });
  await page.setViewportSize({ width: 1600, height: 1000 });
  const captured = await installMocks(page);
  await seedWorkbench(page);
  await page.goto('/');
  await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('workbench-viewer-open-decisions')).toHaveText('1 open');
  await expect(page.getByTestId(`studio-explorer-workbench-group-${PROJECT}-needs-decision`))
    .toContainText('1');

  const cardList = page.getByTestId('dossier-card-list');
  await cardList.getByTestId('dossier-cards-toggle').click();
  await cardList.getByTestId('dossier-card-AGT-2600').click();
  await expect(page.getByTestId('concept-dossier-link')).toHaveAttribute(
    'href', '#/projects/naming-evidence/workbenches/naming-dossier',
  );
  await page.getByTestId('concept-dossier-link').click();
  await expect(page.getByTestId('workbench-viewer')).toBeVisible();

  const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  const stableOption = frame.locator('[data-option-id="option-a"] input');
  await expect(stableOption).toBeVisible();
  await expect(page.getByTestId('workbench-decision-answer-count').first())
    .toContainText('answer every point in the document before recording them together');
  await stableOption.check();
  await frame.locator('[data-studio-decision-comment]')
    .fill('Total soll ein bisschen weiter nach oben abgesetzt werden');
  const inline = page.getByTestId('workbench-inline-decision-action');
  await expect(inline.getByTestId('workbench-decision-start')).toBeEnabled();
  await inline.getByTestId('workbench-decision-start').press('Enter');
  await expect(inline.getByTestId('workbench-decision-goal'))
    .toHaveValue('Choose the stable naming contract used by public references.');
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
  await expect(cardList.getByTestId('dossier-card-count')).toHaveText('2 cards');
  if (await cardList.getByTestId('dossier-card-AGT-2600').count() === 0) {
    await cardList.getByTestId('dossier-cards-toggle').click();
  }
  await expect(cardList.getByTestId('dossier-card-AGT-2600')).toBeVisible();
  await expect(cardList.getByTestId('dossier-card-AGT-2611')).toBeVisible();
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
  await page.reload();
  await expect(page.getByTestId('workbench-viewer')).toBeVisible();
  await expect(page.getByTestId('workbench-viewer-open-decisions')).toHaveText('0 open');
  await expect(page.getByTestId(`studio-explorer-workbench-group-${PROJECT}-needs-decision`))
    .not.toContainText('1');
  await expect(page.getByTestId(`studio-explorer-workbench-group-${PROJECT}-in-implementation`))
    .toContainText('1');
  const reloadedCards = page.getByTestId('dossier-card-list');
  await reloadedCards.getByTestId('dossier-cards-toggle').click();
  await reloadedCards.getByTestId('dossier-card-AGT-2611').click();
  await expect(page.getByTestId('concept-dossier-link')).toHaveAttribute(
    'href', '#/projects/naming-evidence/workbenches/naming-dossier',
  );
  await expect(page.getByTestId('concept-dossier-link')).toHaveText(DOSSIER_PATH);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByTestId('concept-dossier-notice').screenshot({
    path: resolve(RESULTS, 'dossier-spawned-card-link-390--mocked.png'),
  });
  await page.getByTestId('concept-dossier-link').click();
  await expect(page.getByTestId('workbench-viewer')).toBeVisible();
  if (await page.getByTestId('dossier-card-AGT-2600').count() === 0) {
    await page.getByTestId('dossier-cards-toggle').click();
  }
  await page.getByTestId('dossier-card-AGT-2600').click();
  await expect(page.getByTestId('concept-dossier-decision-receipt'))
    .toContainText('Decisions recorded on naming-dossier: 1 of 1; spawned AGT-2611.');
  await expect(page.getByTestId('studio-triage-action-mark-done')).toContainText('Accept');
  await page.getByTestId('concept-dossier-notice').screenshot({
    path: resolve(RESULTS, 'dossier-concept-decision-390--mocked.png'),
  });

  const runtimeErrors: string[] = [];
  page.on('console', message => {
    if (message.type() === 'error' && message.text().includes('TypeError'))
      runtimeErrors.push(message.text());
  });
  await page.goto(`/?job=naming-feature-1&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  const overview = page.getByTestId('overview-status');
  await expect(overview).toBeVisible({ timeout: 30_000 });
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
    dossier: { path: 'docs/operations/naming-dossier/index.html' },
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

test('failed Dossier confirmation reports the card the server removed', async ({ page, devBackend }) => {
  expect(devBackend.port).toBeGreaterThan(0);
  const captured = await installMocks(page);
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}/decisions/confirm`,
    route => {
      captured.decisionBodies.push(JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>);
      return json(route, {
        success: false, errorCode: 'write-failed', removedTaskIds: ['naming-feature-1'], retainedTaskIds: [],
        error: 'The Dossier decision could not be saved. No decision was recorded. Try again or ask a Studio operator to check the project repository. Nothing was created: task AGT-2611 was removed again.',
      }, 500);
    },
  );
  await seedWorkbench(page);
  await page.goto('/');
  const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  await frame.locator('[data-option-id="option-a"] input').check();
  const inline = page.getByTestId('workbench-inline-decision-action');
  await inline.scrollIntoViewIfNeeded();
  await expect.poll(() => inline.evaluate(element => getComputedStyle(element).pointerEvents))
    .toBe('auto');
  await inline.getByTestId('workbench-decision-start').click();
  await inline.getByTestId('workbench-decision-confirm').click();
  await expect(inline.getByRole('alert')).toContainText('task AGT-2611 was removed again');
  await expect(inline.getByRole('alert')).not.toContainText('git config');
  expect(captured.decisionBodies.at(-1)).toEqual(expect.objectContaining({
    spawnedTaskKeys: ['AGT-2611'], spawnedTaskIds: ['naming-feature-1'],
  }));
  expect(captured.deletedTaskIds).toEqual([]);
  await expect(inline.getByTestId('workbench-decision-created-tasks')).toHaveCount(0);
});

test('a restored draft card without an id is never removed by its display key', async ({ page, devBackend }) => {
  expect(devBackend.port).toBeGreaterThan(0);
  const captured = await installMocks(page);
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}/decisions/confirm`,
    route => {
      captured.decisionBodies.push(JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>);
      return json(route, {
        success: false, errorCode: 'write-failed', removedTaskIds: [], retainedTaskIds: [],
        error: 'The Dossier decision could not be saved. No decision was recorded.',
      }, 500);
    },
  );
  await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/[^/?]+(?:\?.*)?$/, route => {
    if (route.request().method() !== 'DELETE') return route.fallback();
    captured.deletedTaskIds.push(new URL(route.request().url()).pathname.split('/').pop() ?? '');
    return json(route, {});
  });
  await seedWorkbench(page);
  await page.addInitScript(({ project, workbenchId }) => {
    if (sessionStorage.getItem('agt3654-draft-seeded')) return;
    sessionStorage.setItem('agt3654-draft-seeded', '1');
    sessionStorage.setItem('atp.workbench-decision-drafts.v1', JSON.stringify({
      version: 1,
      entries: {
        [`${encodeURIComponent(project)}:${encodeURIComponent(workbenchId)}`]: {
          mode: 'feature-spawn', actor: 'Robert', title: 'Implement the stable naming contract',
          goal: 'Apply the selected stable key.', operationId: 'workbench-ui-restored',
          responses: [], updatedAt: '2026-11-10T10:00:00Z',
          createdCard: {
            key: 'AGT-2699', taskKey: `${project}::AGT-2699`,
            title: 'Implement the stable naming contract', lane: '2-ready',
          },
        },
      },
    }));
  }, { project: PROJECT, workbenchId: WORKBENCH_ID });
  await page.goto('/');
  const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  await frame.locator('[data-option-id="option-a"] input').check();
  const inline = page.getByTestId('workbench-inline-decision-action');
  await inline.getByTestId('workbench-decision-confirm').click();
  await expect(inline.getByRole('alert')).toContainText(
    'Task AGT-2699 could not be checked, so it was not removed');
  expect(captured.decisionBodies.at(-1)).toEqual(expect.objectContaining({
    spawnedTaskKeys: ['AGT-2699'], spawnedTaskIds: [],
  }));
  expect(captured.taskBodies).toEqual([]);
  expect(captured.deletedTaskIds).toEqual([]);
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
  await expect(inline.getByTestId('workbench-decision-revision-round'))
    .toContainText('Revision round queued on AGT-2600');
  expect(captured.steerBodies).toHaveLength(0);
  expect(captured.decisionBodies[1]).toEqual(expect.objectContaining({
    outcome: 'rework', cliType: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'high',
  }));
  expect(captured.decisionBodies[1]['revisionPrompt']).toContain(
    'Raise the total and revise the supporting rationale.',
  );
  await page.screenshot({
    path: resolve(RESULTS, 'dossier-inline-decision-rework--mocked.png'),
    fullPage: true,
  });
});

test('unchanged revision round restores Dossier decisions and links its card', async ({ page, devBackend }) => {
  expect(devBackend.port).toBeGreaterThan(0);
  const captured = await installMocks(page);
  await seedWorkbench(page);
  await page.goto('/');
  const inline = page.getByTestId('workbench-inline-decision-action');
  await expect(inline.getByTestId('workbench-decision-rework')).toBeVisible({ timeout: 30_000 });
  await page.frameLocator('[data-testid="workbench-viewer-frame"]')
    .locator('[data-option-id="option-b"] input').check();
  await inline.getByTestId('workbench-decision-rework').click();
  await inline.getByTestId('workbench-decision-rework-request').fill('Check the naming choice again.');
  await inline.getByTestId('workbench-decision-confirm-rework').click();
  await expect(inline.getByTestId('workbench-decision-receipt')).toBeVisible();

  captured.settleUnchanged();
  await page.reload();
  await expect(inline.getByTestId('workbench-decision-revision-round')).toContainText('unchanged');
  await expect(inline.getByTestId('workbench-decision-rework')).toBeVisible();
  await expect(inline.getByTestId('workbench-decision-start')).toBeVisible();
  await expect(inline.getByTestId('workbench-decision-start')).toBeEnabled();
  await expect(inline.getByTestId('workbench-decision-open-revision-card')).toHaveText('Open round AGT-2600');
  mkdirSync(RESULTS, { recursive: true });
  await page.screenshot({ path: resolve(RESULTS, 'agt-3843-unchanged-rework-round.png'), fullPage: true });
  await inline.getByTestId('workbench-decision-open-revision-card').click();
  await expect(page.getByRole('tab', { name: 'Naming concept' })).toContainText('AGT-2600');
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

/**
 * AGT-3703 (playthrough AGT-3649 F10): the owner asks for one more section in
 * the rework form, sees the card that runs the revision, confirms without
 * picking a model, sees the queued round, and finds their choice again after
 * the revision lands.
 */
for (const variant of [
  { name: 'light', theme: 'light' as const, width: 1600, height: 1000 },
  { name: 'dark', theme: 'dark' as const, width: 1600, height: 1000 },
  { name: '390', theme: 'light' as const, width: 390, height: 844 },
]) {
  test(`rework form asks for the request and keeps the owner choice after the revision (${variant.name})`, async ({ page, devBackend }) => {
    expect(devBackend.port).toBeGreaterThan(0);
    mkdirSync(RESULTS, { recursive: true });
    await page.setViewportSize({ width: variant.width, height: variant.height });
    const captured = await installMocks(page);
    await seedWorkbench(page, variant.theme, false);
    await page.goto('/');
    await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });

    const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
    await frame.locator('[data-option-id="option-b"] input').check();
    const inline = page.getByTestId('workbench-inline-decision-action');
    const rework = inline.getByTestId('workbench-decision-rework');
    await expect(rework).toBeEnabled();
    await rework.click();

    const form = inline.getByTestId('workbench-decision-rework-confirmation');
    await expect(form.getByTestId('workbench-decision-revision-card'))
      .toContainText('AGT-2600 · Concept: naming contract');
    await expect(form.getByTestId('workbench-decision-revision-card')).toContainText('Accepted');
    await expect(form.getByTestId('workbench-decision-agent'))
      .toHaveAttribute('aria-label', /claude-opus-4-6/);
    const confirm = form.getByTestId('workbench-decision-confirm-rework');
    await expect(confirm).toBeDisabled();
    await form.getByTestId('workbench-decision-rework-request')
      .fill('Add a privacy section that says what the public name may contain.');
    await expect(confirm).toBeEnabled();
    const request = form.getByTestId('workbench-decision-rework-request');
    const colours = await request.evaluate(element => {
      const style = getComputedStyle(element);
      return { color: style.color, background: style.backgroundColor };
    });
    expect(contrastRatio(colours.color, colours.background), 'request field contrast')
      .toBeGreaterThanOrEqual(4.5);
    await form.scrollIntoViewIfNeeded();
    await page.screenshot({
      path: resolve(RESULTS, `agt-3703-rework-form--${variant.name}.png`),
      fullPage: variant.width > 400,
    });

    await confirm.click();
    const round = inline.getByTestId('workbench-decision-revision-round');
    await expect(round).toContainText('Revision round queued on AGT-2600');
    await expect(round).toContainText('runner slot');
    await expect(inline.getByTestId('workbench-decision-request-receipt'))
      .toContainText('Add a privacy section');
    expect(captured.steerBodies).toHaveLength(0);
    expect(captured.decisionBodies[1]).toEqual(expect.objectContaining({
      outcome: 'rework', confirmed: true, cliType: 'claude', model: 'claude-opus-4-6',
      request: 'Add a privacy section that says what the public name may contain.',
      responses: [expect.objectContaining({ selectedOptionIds: ['option-b'] })],
    }));
    expect(captured.decisionBodies[1]['revisionPrompt'])
      .toContain('Owner request:\nAdd a privacy section');
    await round.scrollIntoViewIfNeeded();
    await page.screenshot({
      path: resolve(RESULTS, `agt-3703-rework-round-queued--${variant.name}.png`),
      fullPage: variant.width > 400,
    });

    captured.landRevision();
    await page.reload();
    await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });
    await expect(frame.getByRole('heading', { name: 'Privacy' })).toBeVisible();
    await expect(frame.locator('[data-option-id="option-b"] input')).toBeChecked();
    await expect(frame.locator('[data-option-id="option-a"] input')).not.toBeChecked();
    await expect(page.getByTestId('workbench-inline-decision-action')
      .getByTestId('workbench-decision-revision-round')).toContainText('updated');
    await expect(page.getByTestId('workbench-inline-decision-action')
      .getByTestId('workbench-decision-rework')).toBeVisible();
    await page.screenshot({
      path: resolve(RESULTS, `agt-3703-revision-keeps-owner-choice--${variant.name}.png`),
      fullPage: variant.width > 400,
    });
  });
}
