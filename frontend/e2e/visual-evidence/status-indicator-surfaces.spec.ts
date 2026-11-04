import { expect, test, type Page, type Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme, type Theme } from '../helpers/theme';
import {
  DEPLOY_FIXTURE_NOW,
  deployDeploymentsFixture,
  deployEnvironmentsFixture,
  deployReleasesFixture,
  deployStatusFixture,
  deploymentLogFixture,
  pipelineDefinitionFixture,
} from '../../src/app/features/project-detail/testing/deploy-fixtures';

/**
 * Acceptance evidence for AGT-3403: every status renders through the shared
 * `<app-status-indicator>`. This spec captures the board, a card detail, the
 * Operations view and the Deploy area in both themes on a fully mocked API.
 *
 * The mock data deliberately mixes states so several tones are on screen at
 * once: a running card, a failed and a stalled card, a card waiting on a
 * dependency, the review queue, a merged delivery with proven test evidence,
 * an integration conflict, a pipeline with an earlier attempt, healthy and
 * unhealthy environments, succeeded, failed, running and awaiting-approval
 * deploy runs, and Operations deploy and rollback markers.
 *
 * Route shapes follow card-state-pill-matches-lane.spec.ts (board) and
 * project-deploy-area.spec.ts (Deploy and Operations).
 */

const PROJECT = 'Website';
const STORAGE = '/srv/tasks/website';
const REPO = '/srv/repo/website';
const DEPLOY = `/api/projects/${PROJECT}/deploy`;
const VIEWPORT = { width: 1440, height: 900 };
const RESULTS = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : path.resolve(__dirname, '..', '..', 'test-results', 'status-indicator-surfaces');

/** Board and detail clock; deploy surfaces keep the deploy fixture clock. */
const BOARD_NOW = Date.parse('2026-11-04T12:00:00Z');
const minutesAgo = (minutes: number): string => new Date(BOARD_NOW - minutes * 60_000).toISOString();

const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

function commit(sha: string, message: string, minutes: number) {
  return {
    sha: sha.repeat(40).slice(0, 40), shortSha: sha.repeat(7).slice(0, 7), message,
    filesChanged: 3, files: ['src/search/index.ts', 'src/search/query.ts', 'README.md'],
    at: minutesAgo(minutes), attribution: 'automatic', confidence: 0.98,
  };
}

function makeTask(id: string, state: string, title: string, order: number, extra: Record<string, unknown> = {}) {
  return {
    id,
    key: id,
    displayKey: id,
    taskKey: `${STORAGE}::${id}`,
    title,
    state,
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: minutesAgo(240),
    enteredLaneAt: minutesAgo(1),
    watchPath: STORAGE,
    projectName: PROJECT,
    folderPath: `${STORAGE}/.orchestrator/tasks/${state}/${id}`,
    lastActivity: minutesAgo(1),
    sessionName: null,
    model: 'claude-opus-4-7',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...extra,
  };
}

function execution(id: string, status: string, extra: Record<string, unknown> = {}) {
  return {
    jobId: id, taskKey: `${STORAGE}::${id}`, processId: 4242, startedAt: minutesAgo(30), status,
    exitCode: null, durationSeconds: null, model: 'claude-opus-4-7', runOutcome: null, ...extra,
  };
}

const RUNNING = makeTask('WEB-41', '3-progress', 'Rebuild the search index', 1, {
  execution: execution('WEB-41', 'running'),
});
const FAILED = makeTask('WEB-42', '3-progress', 'Retry failed image uploads', 2, {
  lastActivity: minutesAgo(40), enteredLaneAt: minutesAgo(45),
  execution: execution('WEB-42', 'failed', { exitCode: 1, runOutcome: 'failed', durationSeconds: 312 }),
});
const WAITING = makeTask('WEB-44', '2-ready', 'Show search suggestions', 1, {
  waitsOn: {
    items: [{ key: 'WEB-41', resolved: true, fulfilled: false, targetJobId: 'WEB-41', targetTitle: RUNNING.title,
      targetState: '3-progress', targetWatchPath: STORAGE }],
    itemCount: 1, openCount: 1, blocked: true, cycleDetected: false,
  },
});
const REVIEW_URGENT = makeTask('WEB-45', '4-auto-review', 'Fix the broken checkout redirect', 1, {
  reviewPriority: 'urgent', reviewQueuePosition: 1,
  summaryState: { status: 'generating', startedAt: minutesAgo(2), finishedAt: null, errorMessage: null, bytesWritten: null },
});
const REVIEW_NORMAL = makeTask('WEB-46', '4-auto-review', 'Add structured data to product pages', 2, {
  reviewPriority: 'normal', reviewQueuePosition: 2,
  summaryState: { status: 'failed', startedAt: minutesAgo(9), finishedAt: minutesAgo(8),
    errorMessage: 'The summary model returned no result.', bytesWritten: null },
});
const MERGED = makeTask('WEB-47', '5-human-review', 'Faster project search', 1, {
  tags: ['code-review:grade-a'],
  commits: [commit('a', 'Index project names once', 70), commit('b', 'Query the cached index', 50)],
  commitCount: 2,
  mergeSignal: { branch: 'task/WEB-47', inIntegration: true, inRelease: false, integrationBranch: 'develop',
    releaseBranch: 'main', integrationSha: 'bbbbbbb', releaseSha: null },
  integration: { status: 'integrated', deliveryRef: 'task/WEB-47', sha: 'bbbbbbb', integrationBranch: 'develop',
    detail: 'Both commits are in develop.', released: false, releaseBranch: 'main',
    verification: { state: 'integrated-verified', sha: 'bbbbbbb', evidence: 'gate-receipt', gateVerdict: 'passed' } },
  testEvidence: {
    runId: 'test-run-118', runCommit: 'bbbbbbb', runState: 'completed', runResult: 'passed', matchQuality: 'perfect',
    direction: 'exact', distance: 0, diffContained: true, evidenceState: 'proven', awaitingEvidence: false,
    summary: 'Tests proven',
    sources: [
      { kind: 'project-test-run', id: 'test-run-118', commit: 'bbbbbbb', result: 'passed', observedAt: minutesAgo(30),
        summary: 'Project test run passed', reason: 'Full suite on the delivered commit.', reportRef: '' },
      { kind: 'review-build-tests', id: 'review-7', commit: 'bbbbbbb', result: 'passed', observedAt: minutesAgo(35),
        summary: 'Review build and tests passed', reason: 'Build and unit tests in review.', reportRef: '' },
    ],
    sourceCount: 2, sourceLabel: 'project run',
  },
});
const CONFLICT = makeTask('WEB-48', '6-completed', 'Rename the pricing route', 1, {
  commits: [commit('c', 'Rename the pricing route', 300)],
  commitCount: 1,
  integration: { status: 'conflict-skipped', deliveryRef: 'task/WEB-48', sha: null, integrationBranch: 'develop',
    detail: 'Merge conflict in src/routes.ts.', failure: { code: 'merge-conflict', rebaseRecoveryAvailable: true } },
  testEvidence: {
    runId: 'test-run-119', runCommit: 'ccccccc', runState: 'completed', runResult: 'failed', matchQuality: 'perfect',
    direction: 'exact', distance: 0, diffContained: true, evidenceState: 'failed', awaitingEvidence: false,
    summary: 'Tests failed', sourceCount: 1, sourceLabel: 'project run',
  },
});
const PENDING = makeTask('WEB-49', '6-completed', 'Compress hero images', 2, {
  commits: [commit('d', 'Compress hero images', 200)],
  commitCount: 1,
  integration: { status: 'pending', deliveryRef: 'task/WEB-49', sha: null, integrationBranch: 'develop',
    detail: 'Waiting for the integration queue.' },
  testEvidence: {
    runId: null, runCommit: null, runState: null, runResult: null, matchQuality: 'none', direction: 'none',
    distance: null, diffContained: false, evidenceState: 'pending', awaitingEvidence: true,
    summary: 'Tests pending', sourceCount: 0, sourceLabel: null,
  },
});

const BOARD = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [WAITING],
  progress: [RUNNING, FAILED], failedPickup: [], review: [],
  autoReview: [REVIEW_URGENT, REVIEW_NORMAL], humanReview: [MERGED], completed: [CONFLICT, PENDING], archive: [],
};

function step(id: string, displayName: string, kind: string) {
  return { id, displayName, kind, runMode: 'sequential', dependsOn: [], idempotent: true, stub: false };
}

const CORE_STEPS = [step('core-agent-run', 'Agent run', 'core')];
const POST_STEPS = [
  step('aspect-code-quality', 'Code quality', 'aspect'),
  step('aspect-tests', 'Tests', 'aspect'),
  step('post-orchestrator-decision', 'Auto-review decision', 'orchestrator'),
  step('post-task-spawner', 'Follow-up cards', 'orchestrator'),
];

function execStep(stepId: string, kind: string, status: string, start: number, end: number | null, extra: Record<string, unknown> = {}) {
  return {
    stepId, kind, status,
    startedAt: minutesAgo(start), completedAt: end === null ? null : minutesAgo(end),
    durationMs: end === null ? 0 : (start - end) * 60_000,
    inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
    ...extra,
  };
}

const DETAIL_PIPELINE = {
  pipeline: {
    id: 'standard-task-pipeline', displayName: 'Standard task pipeline', version: 1,
    pre: [], core: CORE_STEPS, post: POST_STEPS, allSteps: [...CORE_STEPS, ...POST_STEPS],
  },
  execution: {
    pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: MERGED.id, project: PROJECT,
    startedAt: minutesAgo(90), completedAt: minutesAgo(40), attempt: 2,
    previousAttempts: [{
      pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: MERGED.id, project: PROJECT,
      startedAt: minutesAgo(180), completedAt: minutesAgo(140), attempt: 1, previousAttempts: [],
      steps: [
        execStep('core-agent-run', 'core', 'passed', 180, 160),
        execStep('aspect-code-quality', 'aspect', 'failed', 160, 150,
          { verdict: 'concerns', verdictSummary: 'The cache is never invalidated.' }),
        execStep('aspect-tests', 'aspect', 'passed', 160, 152, { verdict: 'pass' }),
        execStep('post-orchestrator-decision', 'orchestrator', 'passed', 150, 140, { verdict: 'reissue' }),
        execStep('post-task-spawner', 'orchestrator', 'skipped', 140, 140),
      ],
    }],
    steps: [
      execStep('core-agent-run', 'core', 'passed', 90, 65),
      execStep('aspect-code-quality', 'aspect', 'passed', 65, 55, { verdict: 'pass' }),
      execStep('aspect-tests', 'aspect', 'passed', 65, 50, { verdict: 'pass' }),
      execStep('post-orchestrator-decision', 'orchestrator', 'passed', 50, 45, { verdict: 'accept' }),
      execStep('post-task-spawner', 'orchestrator', 'skipped', 45, 45),
    ],
  },
  cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
  config: {},
};

const DETAIL = {
  info: MERGED,
  promptMarkdown: '# Faster project search\n\nCache the project index so the search answers in under 100 ms.',
  statusMarkdown: '## Result\n\nThe project index is cached and invalidated on rename.',
  log: [],
  promptHistory: [],
  contextUsage: null,
  reviewEvidence: [],
  summaryState: { status: 'ready', startedAt: minutesAgo(44), finishedAt: minutesAgo(43), errorMessage: null, bytesWritten: 512 },
};

/** Core-first detail read (AGT-2953): the board opens a card from `/core` plus `/details/*`. */
const DETAIL_CORE = {
  state: 'ready', projectId: 'PROJ-WEB', projectName: PROJECT,
  id: MERGED.id, taskKey: MERGED.taskKey, key: MERGED.id,
  watchPath: STORAGE, folderPath: MERGED.folderPath,
  title: MERGED.title, kind: 'task', taskType: 'feature',
  lane: MERGED.state, archiveState: null, enteredLaneAt: minutesAgo(40),
  order: 1, mode: 'coding', released: false, pendingIntent: false, coreVersion: '7',
  pins: { model: 'claude-opus-4-7', modelExplicit: true, thinkingLevel: null, thinkingLevelExplicit: false,
    cliType: 'claude', contextMode: null, useOwnSession: null, allowWebAccess: false, noBranchExpected: false },
  actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
  blocking: { blockerType: null, blockerCondition: null, blockerStatus: null, blockerDescription: null,
    needsInput: null, dependencyBlocked: false, dependencyState: 'ready', dependencies: [], dependsOn: [], blockedBy: [] },
  runtime: { attemptId: null, runnerId: null, runnerName: null, hostname: null, executionStatus: null,
    location: 'none', heartbeatAt: null, leaseState: 'none', leaseId: null },
  runtimeVersion: 'v1',
  statusSummary: { state: 'ready', text: DETAIL.statusMarkdown, originalBytes: 60, hash: 'status', cursor: null },
  prompt: { state: 'ready', text: DETAIL.promptMarkdown, originalBytes: 90, hash: 'prompt', cursor: null, continuationUrl: null },
  timeline: { state: 'ready', events: [], cursor: null, continuationUrl: null },
};

function detailResource(name: string, data: unknown) {
  return { id: MERGED.id, taskKey: MERGED.taskKey, projectId: 'PROJ-WEB', attemptId: null, coreVersion: '7',
    resource: name, version: 'v1', computedAt: minutesAgo(1), state: 'ready', data, reason: null };
}

/** Deploy: Staging is unhealthy and redeploying, Production waits for approval. */
function deployMocks() {
  const deployments = deployDeploymentsFixture().map(item => item.id === 'run-c-stage-redeploy' ? {
    ...item, state: 'running', startedAtUtc: '2026-10-19T11:54:30Z', finishedAtUtc: null,
    steps: [
      { name: 'publish-static', state: 'succeeded', startedAtUtc: '2026-10-19T11:54:30Z', finishedAtUtc: '2026-10-19T11:56:00Z', logRef: 'run-c-stage-redeploy/publish-static' },
      { name: 'health', state: 'running', startedAtUtc: '2026-10-19T11:56:00Z', finishedAtUtc: null, logRef: 'run-c-stage-redeploy/health' },
    ],
  } : item);
  const status = deployStatusFixture().map(item => item.environment.id === 'stage'
    ? { ...item, health: 'unhealthy', currentDeployment: deployments.find(run => run.id === 'run-c-stage-redeploy')! }
    : { ...item, currentDeployment: deployments.find(run => run.id === 'run-c-production')! });
  return { environments: deployEnvironmentsFixture(), status, releases: deployReleasesFixture(), deployments };
}

const OPERATIONS = {
  project: PROJECT, since: '2026-09-20T00:00:00Z', until: '2026-10-20T00:00:00Z',
  environments: [
    { environment: { id: 'production', name: 'Production', kind: 'production' }, telemetryAvailable: false, metrics: null,
      markers: [
        { deploymentId: 'run-a-production', releaseId: 'release-2026.10.18.1', version: '2026.10.18.1',
          atUtc: '2026-10-18T22:10:00Z', trigger: 'deploy', state: 'succeeded', cardKeys: ['WEB-29', 'WEB-30'] },
      ] },
    { environment: { id: 'stage', name: 'Staging', kind: 'staging' }, telemetryAvailable: false, metrics: null,
      markers: [
        { deploymentId: 'run-c-stage-redeploy', releaseId: 'release-2026.10.19.2', version: '2026.10.19.2',
          atUtc: '2026-10-19T11:54:00Z', trigger: 'redeploy', state: 'running', cardKeys: ['WEB-35'] },
        { deploymentId: 'run-a-stage-rollback', releaseId: 'release-2026.10.18.1', version: '2026.10.18.1',
          atUtc: '2026-10-19T08:02:00Z', trigger: 'rollback', state: 'succeeded', cardKeys: [] },
        { deploymentId: 'run-b-stage', releaseId: 'release-2026.10.19.1', version: '2026.10.19.1',
          atUtc: '2026-10-19T08:00:00Z', trigger: 'deploy', state: 'failed', cardKeys: ['WEB-33', 'WEB-34'] },
      ] },
  ],
  dora: { successfulProductionDeploys: 1, deploysPerWeek: 0.25, medianCardToProductionHours: 14,
    cardsMeasured: 2, changeFailureRate: 0.33, medianRecoveryHours: 0.1, recoveriesMeasured: 1 },
};

async function installRoutes(page: Page): Promise<void> {
  const deploy = deployMocks();
  const idPattern = (id: string) => id.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

  await page.route('**/api/**', route => route.fulfill(json([])).catch(() => undefined));
  await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill(json({
    negotiateVersion: 1, connectionId: 'status-indicator-mock', connectionToken: 'status-indicator-mock',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  })));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/crash-recovery/pending', route => route.fulfill(json({ pending: [] })));
  // Shapes the shell reads on every surface; the catch-all `[]` would throw in them.
  await page.route('**/api/tasks/archive**', route => route.fulfill(json({ items: [], total: 0, offset: 0, limit: 50 })));
  await page.route('**/api/workspaces/*/quota/admission', route => route.fulfill({ status: 404, body: '' }));
  await page.route('**/api/v1/management/credential-inventory', route => route.fulfill({ status: 404, body: '' }));
  // No remote-host registry: the Ready card must not read as waiting on an unreachable host.
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({ status: 404, body: '' }));
  await page.route('**/api/watch-paths**', route => route.fulfill(json([
    { name: PROJECT, path: STORAGE, rootPath: REPO, repositoryPath: REPO },
  ])));
  await page.route('**/api/v1/studio/board**', route => route.fulfill(json(BOARD)));
  await page.route('**/api/environment', route => route.fulfill(json({
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  })));
  await page.route('**/api/git/summary**', route => route.fulfill(json([])));
  await page.route(/\/api\/git\/hygiene(\?|$)/, route => route.fulfill(json({})));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => route.fulfill(json({
    projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: RUNNING.id, activeExecution: null, queuedJobIds: [] } },
  })));
  await page.route(/\/workbenches(\?|$)/, route => route.fulfill(json({ items: [] })));
  await page.route('**/api/usage/cockpit**', route => route.fulfill(json({}, 503)));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({ at: minutesAgo(0), ttlSeconds: 600, snapshots: [] })));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ at: minutesAgo(0), sessions: [] })));
  const project = {
    sourceType: 'local-folder', id: 'PROJ-WEB', displayName: PROJECT, shortCode: 'WEB', workspaceId: 'ws',
    color: null, cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: STORAGE,
    repositoryPath: REPO, rootPath: REPO, urls: [], ownershipMappings: [], archived: false, createdAt: '2026-10-01T00:00:00Z',
  };
  await page.route('**/api/v1/workspaces**', route => route.fulfill(json([{ id: 'ws', displayName: 'Workspace', projects: [project] }])));
  await page.route('**/api/v1/projects', route => route.fulfill(json([project])));

  // Card detail of the merged card.
  const merged = idPattern(MERGED.id);
  await page.route(new RegExp(`/api/tasks/${merged}/output(\\?|$)`), route => route.fulfill(json([])));
  await page.route(new RegExp(`/api/tasks/${merged}/runs(\\?|$)`), route => route.fulfill(json({ runs: [] })));
  await page.route(new RegExp(`/api/tasks/${merged}/session-events(\\?|$)`), route => route.fulfill(json({ events: [], sessionChain: [] })));
  await page.route(new RegExp(`/api/tasks/${merged}/pipeline(\\?|$)`), route => route.fulfill(json(DETAIL_PIPELINE)));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${merged}(\\?|$)`), route => route.fulfill(json(DETAIL)));
  await page.route(new RegExp(`/api/tasks/${merged}/plan(\\?|$)`), route => route.fulfill(json({ hasPlan: false })));
  await page.route(new RegExp(`/api/tasks/${merged}/core(\\?|$)`), route => route.fulfill(json(DETAIL_CORE)));
  await page.route(new RegExp(`/api/tasks/${merged}/details/documents(\\?|$)`), route => {
    const name = new URL(route.request().url()).searchParams.get('name');
    return route.fulfill(json(detailResource('documents', {
      name, markdown: name === 'prompt' ? DETAIL.promptMarkdown : DETAIL.statusMarkdown, summaryState: DETAIL.summaryState,
    })));
  });
  await page.route(new RegExp(`/api/tasks/${merged}/details/usage(\\?|$)`), route =>
    route.fulfill(json(detailResource('usage', { contextUsage: null, tokenSummary: null, lastUsage: null }))));
  await page.route(new RegExp(`/api/tasks/${merged}/details/history(\\?|$)`), route =>
    route.fulfill(json(detailResource('history', { promptHistory: [], titleHistory: [], log: [] }))));
  await page.route(new RegExp(`/api/tasks/${merged}/delivery-claim(\\?|$)`), route => route.fulfill(json({
    taskKey: MERGED.taskKey, jobId: MERGED.id, lane: MERGED.state, deliveryRef: 'task/WEB-47',
    integrationBranch: 'develop', releaseBranch: 'main', containmentStatus: 'contained', integrated: true,
    released: false, integratedSha: 'bbbbbbb', mergeCommit: 'eeeeeee', mergeSubject: 'merge(WEB-47): faster project search',
    mergedAt: minutesAgo(38), hasIntegrationRecord: true, class: 'integrated-delivery', findings: [], completionClaim: null,
  })));
  await page.route(new RegExp(`/api/tasks/${merged}/details/review(\\?|$)`), route =>
    route.fulfill(json(detailResource('review', { reviewProjection: null, evidence: null }))));
  await page.route(new RegExp(`/api/tasks/${merged}/details/git(\\?|$)`), route =>
    route.fulfill(json(detailResource('git', { mergeSignal: MERGED.mergeSignal, integration: MERGED.integration,
      publishSignal: null, testEvidence: MERGED.testEvidence, commit: null, commits: MERGED.commits, provenance: null }))));
  await page.route(new RegExp(`/api/tasks/${merged}/details/overview(\\?|$)`), route =>
    route.fulfill(json(detailResource('overview', {
      decision: null, planningSpawn: null, conceptDossier: null, olderBriefDelivery: null, relatedWikiPages: [],
      creationSource: 'operator', completionClaim: null, parkedBlocker: null, needsInput: null,
      references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] }, waitsOn: null, acceptanceAttemptGap: null,
    }))));

  // Deploy area and Operations.
  await page.route(`**/api/projects/${PROJECT}/environments`, route => route.fulfill(json(deploy.environments)));
  await page.route(`**/api/projects/${PROJECT}/environments/*/volumes`, route => route.fulfill(json({
    database: null, volumes: [], schedules: [], audit: [], adapterReady: false,
  })));
  await page.route(`**/api/projects/${PROJECT}/environments/health`, route => route.fulfill(json({
    capturedAt: new Date(DEPLOY_FIXTURE_NOW).toISOString(), probeOrigin: 'mock', environments: [],
  })));
  await page.route(`**${DEPLOY}/status`, route => route.fulfill(json(deploy.status)));
  await page.route(`**${DEPLOY}/releases`, route => route.fulfill(json(deploy.releases)));
  await page.route(url => url.pathname === `${DEPLOY}/releases/page`, route => route.fulfill(json({
    items: deploy.releases, offset: 0, limit: 25, total: deploy.releases.length,
  })));
  await page.route(`**${DEPLOY}/releases/*/notes`, (route: Route) => {
    const id = route.request().url().split('/').at(-2);
    const release = deploy.releases.find(item => item.id === id);
    return release
      ? route.fulfill(json({ releaseId: release.id, version: release.version, preview: true,
        notes: release.cardKeys.map(cardKey => ({ cardKey, text: `Delivered ${cardKey}` })) }))
      : route.fulfill({ status: 404, body: '' });
  });
  await page.route(`**${DEPLOY}/deployments/page?*`, route => {
    const params = new URL(route.request().url()).searchParams;
    let runs = deploy.deployments;
    if (params.has('environmentId')) runs = runs.filter(run => run.environmentId === params.get('environmentId'));
    if (params.has('state')) runs = runs.filter(run => run.state === params.get('state'));
    return route.fulfill(json({ items: runs.slice(0, 25), offset: 0, limit: 25, total: runs.length }));
  });
  await page.route(new RegExp(`${DEPLOY}/deployments/(?!page)[^/?]+$`), route => {
    const id = decodeURIComponent(route.request().url().split('/').at(-1)!);
    const run = deploy.deployments.find(item => item.id === id);
    return run ? route.fulfill(json(run)) : route.fulfill(json({ error: `Unknown deployment '${id}'.` }, 404));
  });
  await page.route(`**${DEPLOY}/deployments/*/log`, route => {
    const id = route.request().url().split('/').at(-2)!;
    return route.fulfill(json({ deploymentId: id, lines: deploymentLogFixture(id) }));
  });
  await page.route(`**/api/projects/${PROJECT}/pipeline-definition`, route => route.fulfill(json(pipelineDefinitionFixture())));
  await page.route(`**${DEPLOY}/operations`, route => route.fulfill(json(OPERATIONS)));
  await page.route(`**${DEPLOY}/operations/logs?*`, route => route.fulfill(json({ telemetryAvailable: false, lines: [] })));
}

type Tab = { kind: 'board'; projectName: string } | { kind: 'hub'; projectName: string; section: string };

async function open(page: Page, tabs: Tab[], activeKey: string, now: number): Promise<void> {
  page.on('dialog', dialog => dialog.accept());
  await page.setViewportSize(VIEWPORT);
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.clock.setFixedTime(new Date(now));
  await page.addInitScript(({ tabs: seeded, activeKey: key }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1, tabs: seeded, activeKey: key }));
  }, { tabs, activeKey });
  await installRoutes(page);
  await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 60_000 });
}

async function hideDevOverlays(page: Page): Promise<void> {
  await page.evaluate(() => document.querySelectorAll('[data-testid="error-dialog-overlay"], vite-error-overlay')
    .forEach(element => ((element as HTMLElement).style.display = 'none')));
}

async function captureBothThemes(page: Page, name: string): Promise<void> {
  fs.mkdirSync(RESULTS, { recursive: true });
  for (const theme of ['light', 'dark'] as Theme[]) {
    await setTheme(page, theme);
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
    await hideDevOverlays(page);
    await page.mouse.move(0, 0);
    await page.screenshot({ path: path.join(RESULTS, `${name}-${theme}.png`), animations: 'disabled', caret: 'hide' });
  }
}

const card = (page: Page, title: string) => page.getByTestId('task-card').filter({ hasText: title });

async function openBoard(page: Page): Promise<void> {
  await open(page, [{ kind: 'board', projectName: '__all__' }], 'board:__all__', BOARD_NOW);
  await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
    .toBeVisible({ timeout: 30_000 });
  await expect(card(page, RUNNING.title)).toBeVisible({ timeout: 30_000 });
  await hideDevOverlays(page);
}

async function openDeploy(page: Page): Promise<void> {
  await open(page, [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName: PROJECT, section: 'deploy' }],
    `hub:${PROJECT}`, DEPLOY_FIXTURE_NOW);
  await hideDevOverlays(page);
  await expect(page.getByTestId('project-deploy-area')).toBeVisible({ timeout: 60_000 });
}

test.describe('Status indicator surfaces (mocked)', () => {
  test.describe.configure({ timeout: 180_000 });

  test('board shows card states through the shared indicator', async ({ page }) => {
    await openBoard(page);

    await expect(card(page, FAILED.title).getByTestId('task-card-execution-status')).toHaveAttribute('data-tone', 'danger');
    await expect(card(page, FAILED.title).getByTestId('task-card-stalled-status')).toHaveAttribute('data-tone', 'warning');
    await expect(card(page, WAITING.title).getByTestId('task-card-waiting-on-status')).toHaveAttribute('data-tone', 'warning');
    await expect(card(page, REVIEW_URGENT.title).getByTestId('task-card-review-queue')).toBeVisible();
    await expect(card(page, MERGED.title).getByTestId('integration-status-indicator')).toHaveAttribute('data-tone', 'success');
    await expect(card(page, MERGED.title).getByTestId('test-evidence-state')).toHaveAttribute('data-tone', 'success');
    await expect(card(page, CONFLICT.title).getByTestId('integration-status-indicator')).toHaveAttribute('data-tone', 'danger');
    await expect(card(page, CONFLICT.title).getByTestId('test-evidence-state')).toHaveAttribute('data-tone', 'danger');
    await expect(card(page, REVIEW_NORMAL.title).getByTestId('task-card-review-status')).toHaveAttribute('data-tone', 'danger');

    // Bring the review queue into the 900 px frame under the failed card.
    await card(page, FAILED.title).evaluate(element => element.scrollIntoView({ block: 'start' }));
    await captureBothThemes(page, 'board');
  });

  test('card detail shows pipeline steps and run history through the shared indicator', async ({ page }) => {
    await openBoard(page);
    await card(page, MERGED.title).first().click();
    const pipeline = page.getByTestId('overview-pipeline');
    const overviewTab = page.getByTestId('prompt-tab-overview');
    if (!(await pipeline.isVisible().catch(() => false))) {
      await expect(overviewTab).toBeVisible({ timeout: 30_000 });
      await overviewTab.click();
    }
    await expect(pipeline).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId('overview-pipeline-run-switcher')).toBeVisible();
    await expect(page.locator('[data-testid="status-indicator"], [data-tone]').first()).toBeVisible();
    await hideDevOverlays(page);

    await captureBothThemes(page, 'card-detail');
  });

  test('Operations shows deploy and rollback markers through the shared indicator', async ({ page }) => {
    await openDeploy(page);
    await page.getByTestId('deploy-tab-operations').click();
    const operations = page.getByTestId('project-operations');
    await expect(operations).toBeVisible();
    const markers = operations.getByTestId('operations-marker-state');
    await expect(markers).toHaveCount(4);
    await expect(operations.getByTestId('operations-production').getByTestId('operations-marker-state'))
      .toHaveAttribute('data-tone', 'success');
    const staging = operations.getByTestId('operations-stage').getByTestId('operations-marker-state');
    await expect(staging.nth(0)).toHaveAttribute('data-tone', 'running');
    await expect(staging.nth(2)).toHaveAttribute('data-tone', 'warning');

    // Start the frame at the Operations section so both environments' markers fit.
    await operations.evaluate(element => element.scrollIntoView({ block: 'start' }));
    await captureBothThemes(page, 'operations');
  });

  test('Deploy shows environment health and run states through the shared indicator', async ({ page }) => {
    await openDeploy(page);
    const staging = page.getByRole('article', { name: 'Staging' });
    const production = page.getByRole('article', { name: 'Production' });
    await expect(staging.getByTestId('deploy-environment-health')).toHaveAttribute('data-tone', 'danger');
    await expect(staging.getByTestId('deploy-environment-last-run-state')).toHaveAttribute('data-tone', 'running');
    await expect(production.getByTestId('deploy-environment-health')).toHaveAttribute('data-tone', 'success');
    await expect(production.getByTestId('deploy-environment-last-run-state')).toHaveAttribute('data-tone', 'info');

    await captureBothThemes(page, 'deploy');
  });
});
