import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

const TASK_ID = 'timeline-density-task';
const TASK_KEY = 'AGT-2412';
const WATCH_PATH = '/tmp/timeline-density';
const PROJECT = 'Timeline Density';
const EVENTS_LENGTH = { value: 0 };

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [], progress: [], failedPickup: [], codeNotComplete: [],
  review: [], autoReview: [], humanReview: [], escalated: [],
  completed: [], archive: [],
};

const TASK_DETAIL = {
  info: {
    id: TASK_ID,
    key: TASK_KEY,
    displayKey: TASK_KEY,
    taskKey: `${WATCH_PATH}::${TASK_ID}`,
    title: 'Timeline event density review',
    state: '3-progress',
    order: 1,
    agent: 'codex',
    createdAt: '2026-07-28T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/3-progress/${TASK_ID}`,
    lastActivity: '2026-07-28T09:00:00Z',
    sessionName: null,
    model: null,
    cliType: null,
    thinkingLevel: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
  },
  promptMarkdown: '# Timeline event density review',
  promptHistory: [],
  titleHistory: [],
  statusMarkdown: null,
  contextUsage: null,
  log: [],
  summaryState: null,
  reviewEvidence: [],
};

const EVENTS = [
  event('prompt_created', 'human:robert@example.com', 'Task created: Timeline event density review', {
    targetState: '1-backlog', agent: 'codex',
  }),
  event('quota_admission_decision', 'system', 'Primary route has sufficient headroom', {
    outcome: 'LaunchPrimary', cli: 'codex', model: 'gpt-5.6-sol',
    isFallback: 'false', projectedPct: '31.2', burnPctPerHour: '3.4',
  }),
  event('load_throttle_decision', 'system', 'Launch deferred while host CPU remains saturated', {
    cpuPercent: '94.2', sustainedSeconds: '60', category: 'environmental-load',
  }),
  event('runner_slot_admission', 'system', 'Admitted to slot 1/2: predicted scope is disjoint', {
    slot: '1', maxParallelism: '2', decision: 'parallel-ok',
  }),
  event('quota_fallback_activated', 'system', 'Fallback: codex/gpt-5.6-terra; reason: quota', {
    primaryCli: 'codex', primaryModel: 'gpt-5.6-sol', fallbackCli: 'codex',
    fallbackModel: 'gpt-5.6-terra', reason: 'quota', quotaDetail: 'weekly window exhausted',
  }),
  event('agent_run_started', 'system', 'codex CLI start', {
    cli: 'codex', model: 'gpt-5.6-sol', quotaFallback: 'false',
    fallbackReason: '', intent: 'start', resumed: 'false',
  }),
  event('run_continued_after_restart', 'system', 'Continuing after restart: reattached live codex run in slot 1/2', {
    recovered: 'true', restartBridge: 'local-durable-worker', cli: 'codex',
    occupied: '1', maxParallelism: '2',
  }),
  event('pre_step_started', 'system', 'Context retrieval started', {
    step: 'context-retrieval', attempt: '1',
  }),
  event('pre_step_finished', 'system', 'Context retrieval completed', {
    step: 'context-retrieval', status: 'completed', durationMs: '1280', exitCode: '0',
  }),
  event('integration_lease', 'system', 'Integration lease granted for develop', {
    outcome: 'granted', integrationBranch: 'develop', leaseId: 'lease-42',
    fencingToken: '7', runnerId: 'agent-runner-01',
  }),
  event('post_step_started', 'system', 'Code-quality review started', {
    step: 'aspect-code-quality', attempt: '1',
  }),
  event('post_step_finished', 'system', 'Code-quality review passed', {
    step: 'aspect-code-quality', status: 'passed', durationMs: '6100',
  }),
  event('orchestrator_steered', 'orchestrator', 'Steered the existing diff', {
    verdict: 'steer', reason: 'One open item remains',
    followUpPrompt: 'Fix only the remaining timeline density issue.',
  }),
  event('steer_timeout_resolved', 'system', 'Steer timeout auto-answered from repository state', {
    outcome: 'auto-answered', answer: 'The requested implementation is present.',
    secondsWaiting: '120', timeoutSeconds: '120',
  }),
  event('agent_run_finished', 'agent', 'codex run completed after 247.6s', {
    cli: 'codex', status: 'completed',
  }),
  event('execution_context', 'system', 'codex context: 3 sources, model gpt-5.6-sol, YOLO', {
    cli: 'codex', source: 'convention', sources: '3', mcp: '0',
  }),
  event('read_only_containment_violation', 'system', 'Planning run changed 2 files', {
    mode: 'planning', files: 'README.md; docs/start/README.md', fileCount: '2',
  }),
  event('quality_loop_reopened', 'quality-loop', 'Re-opened: one visual issue remains', {
    attempt: '2', maxAttempts: '3', gap: 'Source names are not visible.',
  }),
  event('orchestrator_verdict_accepted', 'orchestrator', 'All review aspects pass', {
    verdict: 'accepted',
  }),
  event('orchestrator_escalated', 'orchestrator', 'Escalated after the attempt budget was exhausted', {
    attempt: '3', maxAttempts: '3', reason: 'A product decision is required.',
  }),
  event('human_review_decided', 'human:robert@example.com', 'Human review accepted the delivery', {
    decision: 'accept', reviewer: 'robert@example.com',
  }),
  event('operator_requeued', 'human:robert@example.com', 'Operator reopened the task for fresh assessment: verify density', {
    from: '5-human-review', to: '2-ready', reason: 'verify density',
    attemptEpoch: '2', rotatedArtifacts: '3',
  }),
  event('post_acceptance_review_report_recorded', 'system', 'post-acceptance review report recorded', {
    attemptId: 'review-42', fence: '7', authorityEpoch: '2', outcome: 'pass',
  }),
  event('lane_changed', 'system', '2-ready → 3-progress', {
    from: '2-ready', to: '3-progress',
  }),
  event('epic_decomposed', 'agent', 'Epic decomposed into 3 tasks', {
    created: '3', targetState: '1-backlog',
  }),
  event('task_spawned', 'orchestrator', 'Spawned WEB-123 in Website', {
    targetProject: 'Website', targetKey: 'WEB-123', targetJobId: 'website-follow-up',
    reason: 'The change belongs to the website project.',
  }),
  event('merged_in', 'human:robert@example.com', 'Linked AGT-2401 into this card (link-only). Reason: duplicate report', {
    secondaryId: 'AGT-2401', mode: 'link-only', reason: 'duplicate report',
  }),
  event('external_completion', 'external', 'Completed externally by operator-chat', {
    source: 'operator-chat', targetState: '5-human-review',
  }),
  event('integration_pending_warning', 'system', 'Accepted, but NOT integrated into develop: the accepted work is not yet merged.', {
    status: 'pending', integrationBranch: 'develop', resultRef: 'refs/heads/task/timeline-density',
  }),
  event('integration_recovery_queued', 'system', 'Integration recovery queued: rebase task/timeline-density onto develop.', {
    deliveryRef: 'task/timeline-density', resultSha: '1234567890abcdef',
    integrationBranch: 'develop', mode: 'steer',
  }),
];

const EXECUTION_SOURCES = [
  {
    kind: 'memory', label: 'Project instructions', path: '/tmp/timeline-density/AGENTS.md',
    exists: true, detail: null,
  },
  {
    kind: 'instruction-file', label: 'Frontend instructions',
    path: '/tmp/timeline-density/frontend/AGENTS.md', exists: true, detail: null,
  },
  {
    kind: 'global-config', label: 'Codex config', path: '/home/operator/.codex/config.toml',
    exists: true, detail: null,
  },
];

function event(kind: string, actor: string, summary: string, details?: Record<string, string>) {
  const index = EVENTS_LENGTH.value++;
  return {
    ts: new Date(Date.UTC(2026, 6, 28, 8, index)).toISOString(),
    kind,
    actor,
    runId: kind.includes('run_') || kind === 'execution_context' ? 'run-42' : null,
    summary,
    details,
  };
}

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function stubApp(page: Page, activeEvent: () => (typeof EVENTS)[number]): Promise<void> {
  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const pathname = url.pathname;

    if (pathname === '/api/auth/status') {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (/\/api\/cli\/[^/]+\/models$/.test(pathname)) {
      return json(route, { models: [], source: 'timeline-e2e' });
    }
    if (pathname === '/api/watch-paths') {
      return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    }
    if (pathname === '/api/tasks/grouped') {
      return json(route, EMPTY_GROUPED);
    }
    if (pathname === '/api/tasks/archive') return json(route, { items: [], total: 0 });
    if (pathname === `/api/tasks/${TASK_KEY}` || pathname === `/api/tasks/${TASK_ID}`) {
      return json(route, TASK_DETAIL);
    }
    if (/\/api\/tasks\/[^/]+\/timeline$/.test(pathname)) return json(route, [activeEvent()]);
    if (/\/api\/tasks\/[^/]+\/runs$/.test(pathname)) {
      return json(route, {
        runCount: 1,
        firstStartedAt: '2026-07-28T08:05:00Z',
        lastActivityAt: '2026-07-28T08:15:00Z',
        hasActiveRun: false,
        runs: [],
      });
    }
    if (pathname === '/api/runner/status') {
      return json(route, {
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'manual',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      });
    }
    if (pathname === '/api/runner/global') return json(route, { mode: 'paused', activeProjects: [] });
    if (pathname === '/api/runner/links') return json(route, []);
    if (pathname === '/api/v1/management/links'
      || pathname === '/api/v1/management/remote-hosts') return json(route, []);
    if (pathname === '/api/crash-recovery/pending') return json(route, { pending: [] });
    if (pathname === '/api/cli/quota') return json(route, { snapshots: [], ttlSeconds: 600 });
    if (pathname === '/api/cli/usage') return json(route, { items: [] });
    if (pathname === '/api/environment') {
      return json(route, {
        isDev: false,
        devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
      });
    }
    if (pathname === '/api/workspaces') {
      return json(route, []);
    }
    if (pathname === '/api/tasks' || pathname === '/api/projects') return json(route, []);
    if (pathname === '/api/tags' || pathname === '/api/clients'
      || pathname === '/api/clients/' || pathname === '/api/agent-rules'
      || pathname === '/api/epics' || pathname === '/api/git/summary') {
      return json(route, []);
    }
    if (pathname === '/api/epics/completed/count') return json(route, { count: 0 });
    if (pathname.startsWith('/api/runner/token-summary-aggregate')) {
      return json(route, {
        projects: 0, orchestratorEntries: 0, orchestratorLlmCalls: 0,
        totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0,
        totalCacheCreationTokens: 0, estimatedApiCostUsd: 0, allModelsPriced: true,
        byModel: [], byProject: [], fetchedAt: '2026-07-28T09:00:00Z', disclaimer: '',
      });
    }
    if (pathname.startsWith('/api/workspace/tokens/timeline')) {
      return json(route, {
        windowStart: '2026-07-27T09:00:00Z', windowEnd: '2026-07-28T09:00:00Z',
        windowHours: 24, bucketMinutes: 60, bucketCount: 0, cells: [], projects: [],
        fetchedAt: '2026-07-28T09:00:00Z', disclaimer: '',
      });
    }
    if (pathname.startsWith('/api/workspace/tokens/expensive-jobs')) return json(route, { jobs: [] });
    if (pathname.startsWith('/api/adhoc-usage')) {
      return json(route, {
        calls: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0,
        cacheCreationTokens: 0, estimatedApiCostUsd: 0, allModelsPriced: true,
        bySource: [], byDay: [], byModel: [], logPath: '', logSizeBytes: 0,
        logModifiedAt: null, disclaimer: '',
      });
    }
    if (/\/api\/bus\/[^/]+\/messages$/.test(pathname)) return json(route, []);
    if (pathname.includes('/screenshots')) return json(route, { screenshots: [] });
    if (pathname.includes('/pipeline')) return json(route, null);
    if (pathname.includes('/session-events')) return json(route, { events: [], sessionChain: [] });
    if (pathname.includes('/claude-session')) return json(route, null);
    if (pathname.includes('/agent-work-summary')) {
      return json(route, {
        calls: 0, recovered: false, toolCalls: 0, toolCounts: [],
        startedAt: null, lastTouchAt: null, currentSessionId: null,
      });
    }
    if (pathname.includes('/plan')) {
      return json(route, {
        hasPlan: false, source: null, snapshotCount: 0, activeItemId: null,
        softEstimateMedian: null, items: [], unassignedSubActions: [],
      });
    }
    if (pathname.includes('/output')) return json(route, []);
    return json(route, request.method() === 'GET' ? {} : {});
  });
}

function evidencePath(testInfo: TestInfo, phase: string, name: string): string {
  const root = process.env['JOB_RESULTS_DIR']?.trim()
    ? path.resolve(process.env['JOB_RESULTS_DIR'])
    : testInfo.outputDir;
  const folder = path.join(root, 'timeline-events', phase);
  fs.mkdirSync(folder, { recursive: true });
  return path.join(folder, name);
}

const phase = process.env['TIMELINE_EVIDENCE_PHASE'] === 'before' ? 'before' : 'after';
const evidenceTheme: Theme = process.env['TIMELINE_EVIDENCE_THEME'] === 'dark' ? 'dark' : 'light';
const evidenceFolder = evidenceTheme === 'dark' ? `${phase}-dark` : phase;
const requestedKinds = (process.env['TIMELINE_EVENT_KIND'] ?? '')
  .split(',')
  .map(kind => kind.trim())
  .filter(Boolean);
const fixtures = requestedKinds.length > 0
  ? EVENTS.filter(eventFixture => requestedKinds.includes(eventFixture.kind))
  : EVENTS;

if (fixtures.length !== (requestedKinds.length || EVENTS.length)) {
  throw new Error(`Unknown TIMELINE_EVENT_KIND: ${requestedKinds.join(',')}`);
}

for (const eventFixture of fixtures) {
  test(`Timeline ${eventFixture.kind} stays quiet, specific, and non-redundant`, async ({ page }, testInfo) => {
    let selected = eventFixture;
    if (phase === 'after' && eventFixture.kind === 'execution_context') {
      selected = {
        ...eventFixture,
        details: {
          ...eventFixture.details,
          model: 'gpt-5.6-sol',
          thinkingLevel: 'medium',
          sourceItems: JSON.stringify(EXECUTION_SOURCES),
        },
      };
    }
    await stubApp(page, () => selected);
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto(
      `/#/tasks/${TASK_KEY}?view=timeline%3Aprotocol`,
      { waitUntil: 'commit' },
    );
    const timelineTab = page.getByTestId('prompt-tab-timeline');
    await expect(timelineTab).toBeVisible();
    await expect(timelineTab).toHaveAttribute('aria-selected', 'true');
    const row = page.getByTestId('timeline-event');
    await expect(row).toHaveCount(1);
    await setTheme(page, evidenceTheme);
    await dismissDevErrorDialog(page);

    if (phase === 'after' && selected.kind === 'execution_context') {
      await expect(row).toContainText('Modelgpt-5.6-sol');
      await expect(row).toContainText('Thinkingmedium');
      await expect(row).toContainText('3 sources');
      await expect(row).toContainText('Codex config conventions');
      const sources = row.getByTestId('timeline-event-sources');
      await sources.evaluate(element => {
        (element as HTMLDetailsElement).open = true;
      });
      await expect(sources).toHaveAttribute('open', '');
      await expect(row).toContainText('Project instructions');
      await expect(row).toContainText('Frontend instructions');
      await expect(row).toContainText('Codex config');
    }

    await row.screenshot({
      path: evidencePath(testInfo, evidenceFolder, `${selected.kind}.png`),
    });

    if (phase === 'after' && selected.kind === 'execution_context') {
      await expect(row).not.toContainText('YOLO');
      await expect(row).not.toContainText('mcp 0');
    }

    if (phase === 'after' && selected.kind === 'agent_run_finished') {
      await expect(row.getByTestId('timeline-event-kind')).toContainText('Run finished');
      await expect(row.getByTestId('timeline-event-summary')).toHaveCount(0);
    }

    if (selected.kind === 'run_continued_after_restart') {
      await expect(row.getByTestId('timeline-event-kind')).toContainText('Continuing after restart');
      await expect(row).toContainText('local-durable-worker');
    }
  });
}
