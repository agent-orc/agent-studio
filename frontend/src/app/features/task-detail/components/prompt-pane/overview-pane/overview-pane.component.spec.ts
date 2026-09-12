import { afterEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RunTimelinePollService } from '../../../../polling/services/run-timeline-poll.service';
import { AgentWorkSummaryPollService } from '../../../../polling/services/agent-work-summary-poll.service';
import { TaskPipelinePollService } from '../../../../polling/services/task-pipeline-poll.service';
import { TaskTimelinePollService } from '../../../../polling/services/task-timeline-poll.service';
import { OverviewPaneComponent } from './overview-pane.component';
import { TaskState, type TaskInfo } from '../../../../../models/task.model';
import type { AgentWorkSummary } from '../../../../session-events';
import type { PipelineCostSummary, PipelineStepCost, TaskPipelineResponse } from '../../../../task-pipeline';
import type { RunRecord, RunTimeline } from '../../../../run-timeline';
import type { TaskTimelineEvent } from '../../../../task-timeline';

/** A pipeline catalogue + execution with a single core Agent-execution row. */
function agentPipeline(coreModel: string | null = 'claude-opus-4-8'): TaskPipelineResponse {
  return {
    pipeline: {
      id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
      pre: [], core: [], post: [],
      allSteps: [
        { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
        { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
      ],
    },
    execution: {
      pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
      startedAt: new Date().toISOString(), completedAt: null,
      steps: [
        { stepId: 'core-agent-run', kind: 'core', model: coreModel ?? undefined, status: 'running', startedAt: new Date().toISOString(), completedAt: null, durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
      ],
    },
    cost: emptyCost(),
    config: {},
  };
}

function emptyCost(overrides: Partial<PipelineCostSummary> = {}): PipelineCostSummary {
  return {
    steps: [],
    totalInputTokens: 0,
    totalOutputTokens: 0,
    totalCacheReadTokens: 0,
    totalCacheCreationTokens: 0,
    totalTokens: 0,
    totalInputCostUsd: 0,
    totalOutputCostUsd: 0,
    totalCacheReadCostUsd: 0,
    totalCacheCreationCostUsd: 0,
    totalCostUsd: 0,
    anyModelUnknown: false,
    ...overrides,
  };
}

function stepCost(overrides: Partial<PipelineStepCost> & Pick<PipelineStepCost, 'stepId' | 'kind'>): PipelineStepCost {
  return {
    modelKnown: true,
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    totalTokens: 0,
    inputCostUsd: 0,
    outputCostUsd: 0,
    cacheReadCostUsd: 0,
    cacheCreationCostUsd: 0,
    costUsd: 0,
    ...overrides,
  };
}

function runRecord(overrides: Partial<RunRecord> = {}): RunRecord {
  return {
    index: 0, intent: 'start', startedAt: new Date().toISOString(), endedAt: null,
    status: 'completed', cli: 'claude', exitCode: 0, durationSeconds: 12,
    inputSessionId: null, capturedSessionId: null, resumed: false, reason: null,
    userFollowup: null, lineStart: null, lineEnd: null,
    headShaBefore: null, headShaAfter: null, contextRef: null,
    ...overrides,
  };
}

function runTimeline(runCount: number, runs: RunRecord[] = [], extra: Partial<RunTimeline> = {}): RunTimeline {
  return {
    runCount,
    firstStartedAt: runs[0]?.startedAt ?? null,
    lastActivityAt: runs[runs.length - 1]?.startedAt ?? null,
    hasActiveRun: false,
    runs,
    ...extra,
  };
}

function baseJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'test-1', taskKey: 'wp::test-1', key: 'wp::test-1', title: 'Test', state: '2-ready',
    order: 1, agent: 'human', createdAt: new Date().toISOString(),
    watchPath: '/tmp', projectName: 'test', folderPath: '/tmp/test-1',
    lastActivity: new Date().toISOString(), sessionName: null,
    model: null, cliType: null, useOwnSession: null, lastUsage: null,
    execution: null, commit: null,
    ...overrides,
  };
}

async function build(job: TaskInfo, agentWork: AgentWorkSummary | null = null) {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [OverviewPaneComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      RunTimelinePollService,
      AgentWorkSummaryPollService,
      TaskPipelinePollService,
      TaskTimelinePollService,
    ],
  }).compileComponents();
  if (agentWork) {
    TestBed.inject(AgentWorkSummaryPollService).summary.set(agentWork);
  }
  const fixture = TestBed.createComponent(OverviewPaneComponent);
  fixture.componentRef.setInput('job', job);
  try { fixture.detectChanges(); } catch (e) {
    console.warn('[smoke] OverviewPaneComponent initial render skipped:', (e as Error).message);
  }
  return fixture;
}

afterEach(() => {
  TestBed.resetTestingModule();
});

describe('OverviewPaneComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    const fixture = await build(baseJob());
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('shows orchestrator creation provenance in the visible task heading', async () => {
    const fixture = await build(baseJob({ creationSource: 'orchestrator', createdBy: 'Orchestrator' }));
    const provenance = fixture.nativeElement.querySelector(
      '[data-testid="overview-created-by-orchestrator"]',
    ) as HTMLElement | null;

    expect(provenance?.textContent).toContain('Created by Orchestrator');
  });

  it('tokens: standalone section is removed even when lastUsage exists', async () => {
    const fixture = await build(baseJob({
      state: '4-auto-review',
      lastUsage: { at: new Date().toISOString(), tokens: '12.4k', changes: '5 files', requests: '8' },
    }));
    expect(fixture.nativeElement.querySelector('[data-testid="overview-tokens"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="overview-tokens-agent"]')).toBeNull();
  });

  it('tokens: no standalone placeholder exists when no token data exists; Runs section is also absent', async () => {
    const fixture = await build(baseJob({ state: '2-ready' }));
    const c = fixture.componentInstance;
    expect(c.runCount()).toBe(0);
    expect(c.totalDuration()).toBe(0);
    // The whole section is absent — and the old empty-state placeholder is gone.
    expect(fixture.nativeElement.querySelector('[data-testid="overview-tokens"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="overview-tokens-empty"]')).toBeNull();
    // No runs either -> the consolidated Runs section is also absent.
    expect(fixture.nativeElement.querySelector('[data-testid="overview-runs"]')).toBeNull();
  });

  it('tokens: a CLI footer (lastUsage) alone does not render a separate token section', async () => {
    const fixture = await build(baseJob({
      state: '4-auto-review',
      lastUsage: { at: new Date().toISOString(), tokens: '12.4k', changes: null, requests: null },
    }));
    expect(fixture.nativeElement.querySelector('[data-testid="overview-tokens"]')).toBeNull();
  });

  it('status block: combines model selector and CLI into one Agent row', async () => {
    const fixture = await build(baseJob({
      cliType: 'claude',
      model: 'claude-opus-4-8',
    }));
    const host = fixture.nativeElement as HTMLElement;
    const labels = Array.from(host.querySelectorAll<HTMLElement>('[data-testid="overview-status"] .ov-label'))
      .map(el => el.textContent?.trim());

    expect(labels).toContain('Agent');
    expect(labels).toContain('Owner');
    expect(labels).not.toContain('Model');
    expect(labels).not.toContain('CLI');
    expect(host.querySelector('[data-testid="overview-agent-cli"]')?.textContent).toContain('Claude Code');
  });

  it.each([TaskState.Completed, TaskState.Archive])(
    'status block: keeps the agent selection read-only in terminal state %s',
    async (state) => {
      const fixture = await build(baseJob({
        state,
        cliType: 'codex',
        model: 'gpt-5.6-sol',
        thinkingLevel: 'high',
      }));
      const trigger = fixture.nativeElement.querySelector(
        '[data-testid="overview-agent"] [data-testid="chat-compose-model"]',
      ) as HTMLButtonElement;

      expect(trigger).not.toBeNull();
      expect(trigger.disabled).toBe(true);
      trigger.click();
      fixture.detectChanges();
      expect(
        fixture.nativeElement.querySelector(
          '[data-testid="overview-agent"] [data-testid="chat-model-picker"]',
        ),
      ).toBeNull();
    },
  );

  it('status block: keeps the agent selection editable before delivery', async () => {
    const fixture = await build(baseJob({
      state: TaskState.Ready,
      cliType: 'codex',
      model: 'gpt-5.6-sol',
      thinkingLevel: 'high',
    }));
    const trigger = fixture.nativeElement.querySelector(
      '[data-testid="overview-agent"] [data-testid="chat-compose-model"]',
    ) as HTMLButtonElement;

    expect(trigger.disabled).toBe(false);
  });

  it('pipeline block: hides pending step switches once the task reaches human review', async () => {
    const makePipeline = (): TaskPipelineResponse => ({
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'pre-model-qualification', displayName: 'Model qualification', kind: 'module', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: null,
      cost: emptyCost(),
      config: { 'pre-model-qualification': { enabled: true, canDisable: true } },
    });

    const ready = await build(baseJob({ state: TaskState.Ready }));
    TestBed.inject(TaskPipelinePollService).pipeline.set(makePipeline());
    ready.componentInstance.expandAllPipelineGroups();
    ready.detectChanges();
    expect(ready.nativeElement.querySelector('[data-testid="pipeline-step-toggle-pre-model-qualification"]'))
      .not.toBeNull();

    const review = await build(baseJob({ state: TaskState.HumanReview }));
    TestBed.inject(TaskPipelinePollService).pipeline.set(makePipeline());
    review.componentInstance.expandAllPipelineGroups();
    review.detectChanges();
    expect(review.nativeElement.querySelector('[data-testid="pipeline-step-toggle-pre-model-qualification"]'))
      .toBeNull();
  });

  it('pipeline block: exposes the recorded reason behind an escalated open-items check', async () => {
    const fixture = await build(baseJob({ state: TaskState.HumanReview }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'pre-reissue-open-items', displayName: 'Reissue open-items check', kind: 'module', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: '2026-07-13T10:00:00Z', completedAt: '2026-07-13T10:00:01Z',
        steps: [{
          stepId: 'pre-reissue-open-items', kind: 'module', status: 'failed',
          startedAt: '2026-07-13T10:00:00Z', completedAt: '2026-07-13T10:00:01Z', durationMs: 1_000,
          inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
          verdict: 'escalate',
          verdictSummary: '2 open item(s): missing browser evidence; failing integration test',
        }],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    fixture.componentInstance.expandAllPipelineGroups();
    fixture.detectChanges();

    const row = fixture.componentInstance.pipelineRows()[0];
    expect(row.concernTooltip?.title).toBe('Reissue open-items check · Escalation reason');
    expect(row.concernTooltip?.body).toContain('missing browser evidence');
  });

  it('runs section: a recorded run renders count + total duration (folded out of Tokens), Tokens stays hidden', async () => {
    const fixture = await build(baseJob({ state: '6-completed' }));
    TestBed.inject(RunTimelinePollService).timeline.set(
      runTimeline(2, [runRecord({ durationSeconds: 10 }), runRecord({ durationSeconds: 5 })]),
    );
    try { fixture.detectChanges(); } catch { /* ignore */ }
    const c = fixture.componentInstance;
    expect(c.runCount()).toBe(2);
    // Runs moved out of Tokens & Performance: with no token data that section is gone.
    expect(fixture.nativeElement.querySelector('[data-testid="overview-tokens"]')).toBeNull();
    // The single Runs section carries the count + duration summary and the strip.
    expect(fixture.nativeElement.querySelector('[data-testid="overview-runs"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="overview-runs-count"]')?.textContent).toContain('2 runs');
    expect(fixture.nativeElement.querySelector('[data-testid="overview-runs-duration"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="overview-run-row"]').length).toBe(2);
  });

  it('runs section: a killed run with only a CORE-step duration shows the duration, no run count, no strip (ASS-665)', async () => {
    // ASS-665/675: no run rows, but the CORE pipeline step persisted elapsed
    // time. The Runs section must still appear and surface the duration even
    // though the run-timeline carries no run count.
    const fixture = await build(baseJob({ state: '5-human-review' }));
    const pipe = agentPipeline();
    pipe.execution!.steps[0] = {
      ...pipe.execution!.steps[0],
      status: 'failed',
      durationMs: 1_215_900,
      completedAt: new Date().toISOString(),
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }
    const c = fixture.componentInstance;
    expect(c.runCount()).toBe(0);
    expect(c.totalDuration()).toBeCloseTo(1215.9, 1);
    expect(fixture.nativeElement.querySelector('[data-testid="overview-runs"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="overview-runs-duration"]')).not.toBeNull();
    // No run recorded -> no count and no status strip.
    expect(fixture.nativeElement.querySelector('[data-testid="overview-runs-count"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="overview-run-row"]')).toBeNull();
  });

  it('agent-work block surfaces call count + tool counts from the poll service', async () => {
    const fixture = await build(
      baseJob({ state: '6-completed', sessionName: 'sess-1' }),
      {
        calls: 3,
        recovered: false,
        toolCalls: 42,
        toolCounts: [
          { tool: 'Read', count: 24 },
          { tool: 'Edit', count: 12 },
          { tool: 'Bash', count: 6 },
        ],
        startedAt: new Date(Date.now() - 60_000).toISOString(),
        lastTouchAt: new Date().toISOString(),
        currentSessionId: 'sess-1',
      },
    );
    const c = fixture.componentInstance;
    expect(c.hasAgentWork()).toBe(true);
    expect(c.agentWork()!.calls).toBe(3);
    expect(c.topToolCounts().map(tc => tc.tool)).toEqual(['Read', 'Edit', 'Bash']);
    expect(c.toolCountsTooltip()).toContain('Read: 24');
    expect(c.sessionDebugTooltip()).toContain('sess-1');
  });

  it('agent-work block hides when there is no work yet', async () => {
    const fixture = await build(baseJob({ state: '2-ready' }), {
      calls: 0,
      recovered: false,
      toolCalls: 0,
      toolCounts: [],
      startedAt: null,
      lastTouchAt: null,
      currentSessionId: null,
    });
    expect(fixture.componentInstance.hasAgentWork()).toBe(false);
  });

  it('hero title block: displayedTitle falls back to job.id when title is missing', async () => {
    const fixture = await build(baseJob({ title: '', id: 'fallback-task-id' }));
    expect(fixture.componentInstance.displayedTitle()).toBe('fallback-task-id');
  });

  it('hero title block: startTitleEdit seeds the draft and flips editingTitle', async () => {
    const fixture = await build(baseJob({ title: 'Original title' }));
    const c = fixture.componentInstance;
    expect(c.editingTitle()).toBe(false);
    c.startTitleEdit();
    expect(c.editingTitle()).toBe(true);
    expect(c.titleDraft()).toBe('Original title');
    c.cancelTitleEdit();
    expect(c.editingTitle()).toBe(false);
  });

  it('hero title block: saving an unchanged title just exits edit mode (no PUT, no override)', async () => {
    const fixture = await build(baseJob({ title: 'Same title' }));
    const c = fixture.componentInstance;
    c.startTitleEdit();
    c.onTitleDraftInput('   Same title   ');
    c.saveTitle();
    expect(c.editingTitle()).toBe(false);
    // displayedTitle still reflects the underlying job because no optimistic
    // override was set.
    expect(c.displayedTitle()).toBe('Same title');
  });

  it('pipeline block: joins catalogue + execution + cost into per-step rows and a task total', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
          { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
          { id: 'aspect-tests-and-evidence', displayName: 'Tests and evidence', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
        steps: [
          { stepId: 'aspect-code-quality', kind: 'aspect', model: 'claude-haiku-4-5', status: 'passed', durationMs: 1200, inputTokens: 1000, outputTokens: 200, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'pass' },
        ],
      },
      cost: emptyCost({
        steps: [
          stepCost({ stepId: 'aspect-code-quality', kind: 'aspect', model: 'claude-haiku-4-5', modelKnown: true, inputTokens: 1000, outputTokens: 200, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 1200, costUsd: 0.002 }),
        ],
        totalTokens: 1200, totalCostUsd: 0.002, anyModelUnknown: false,
      }),
      // tests-and-evidence disabled at project level; code-quality forced to Haiku.
      config: {
        'aspect-tests-and-evidence': { enabled: false, model: null, mode: null },
        'aspect-code-quality': { enabled: true, model: 'claude-haiku-4-5', mode: null },
      },
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const rows = c.pipelineRows();
    expect(rows.map(r => r.id)).toEqual(['core-agent-run', 'aspect-code-quality', 'aspect-tests-and-evidence']);

    const core = rows.find(r => r.id === 'core-agent-run')!;
    expect(core.status).toBe('pending'); // no execution row, not a stub

    const cq = rows.find(r => r.id === 'aspect-code-quality')!;
    expect(cq.status).toBe('passed');
    expect(cq.model).toBe('claude-haiku-4-5');
    expect(cq.verdict).toBe('pass');
    expect(cq.totalTokens).toBe(1200);
    expect(cq.costKnown).toBe(true);

    const tests = rows.find(r => r.id === 'aspect-tests-and-evidence')!;
    expect(tests.status).toBe('disabled');
    expect(tests.enabled).toBe(false);
    expect(c.disabledPipelineStepCount()).toBe(1);
    expect(c.visiblePipelineRows().map(r => r.id))
      .toEqual(['core-agent-run', 'aspect-code-quality', 'aspect-tests-and-evidence']);

    expect(c.hasPipeline()).toBe(true);
    expect(c.pipelineTotal()).toEqual(expect.objectContaining({ totalTokens: 1200, totalCostUsd: 0.002, anyModelUnknown: false }));
    expect(c.formatCost(0.002)).toBe('$0.0020');
    expect(c.formatCost(1.5)).toBe('$1.50');

    // The ASPECT section defaults collapsed once its work is done (quiet,
    // finished, not the frontier); expand it so the per-step cells are in the DOM.
    c.expandAllPipelineGroups();
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="overview-pipeline-toggle-disabled"]')).not.toBeNull();
    const stepUsage = host.querySelector('[data-step-id="aspect-code-quality"] [data-testid="overview-pipeline-step-tokens"]');
    const stepCostCell = host.querySelector('[data-step-id="aspect-code-quality"] [data-testid="overview-pipeline-step-cost"]');
    expect(stepUsage?.textContent?.trim()).toBe('1.2k');
    expect(stepCostCell?.textContent?.trim()).toBe('$0.0020');
    expect(host.querySelector('[data-testid="overview-pipeline-tokens-by-model"]')).toBeNull();

    c.toggleDisabledPipelineSteps();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(c.hideDisabledPipelineSteps()).toBe(true);
    expect(c.visiblePipelineRows().map(r => r.id)).toEqual(['core-agent-run', 'aspect-code-quality']);
    expect(c.visiblePipelineRows().find(r => r.id === 'aspect-code-quality')?.startsPhase).toBe(true);
    expect(host.querySelector('[data-step-id="aspect-tests-and-evidence"]')).toBeNull();
  });

  it('pipeline totals render no price data instead of a silent zero when every used model is unpriced', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe = agentPipeline('future-unpriced-model');
    pipe.execution!.steps[0] = {
      ...pipe.execution!.steps[0],
      status: 'passed',
      inputTokens: 500,
      outputTokens: 200,
    };
    pipe.cost = emptyCost({
      steps: [stepCost({
        stepId: 'core-agent-run', kind: 'core', model: 'future-unpriced-model',
        modelKnown: false, inputTokens: 500, outputTokens: 200, totalTokens: 700,
      })],
      totalInputTokens: 500,
      totalOutputTokens: 200,
      totalTokens: 700,
      totalCostUsd: 0,
      anyModelUnknown: true,
    });
    pipe.tokensByModel = {
      runs: [{
        attempt: 1, current: true, startedAt: pipe.execution!.startedAt,
        completedAt: pipe.execution!.completedAt,
        models: [{
          model: 'future-unpriced-model', modelKnown: false, steps: 1,
          inputTokens: 500, outputTokens: 200, cacheReadTokens: 0,
          cacheCreationTokens: 0, totalTokens: 700, costUsd: 0,
        }],
        totalTokens: 700, totalCostUsd: 0, anyModelUnknown: true,
      }],
      totalByModel: [{
        model: 'future-unpriced-model', modelKnown: false, steps: 1,
        inputTokens: 500, outputTokens: 200, cacheReadTokens: 0,
        cacheCreationTokens: 0, totalTokens: 700, costUsd: 0,
      }],
      totalTokens: 700,
      totalCostUsd: 0,
      anyModelUnknown: true,
    };

    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const currentTotal = host.querySelector('[data-testid="overview-pipeline-total-cost"]');
    const lifetimeTotal = host.querySelector('[data-testid="pipeline-token-usage-grand-total-cost"]');
    expect(currentTotal?.textContent?.trim()).toBe('- no price data');
    expect(lifetimeTotal?.textContent?.trim()).toBe('- no price data');
    expect(host.textContent).not.toContain('$0.00');
  });

  it('pipeline block: step rows surface a per-step prompt trigger fed from the step-prompts read-model', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'aspect-requirement-fit', displayName: 'Requirement fit', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
          { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
        steps: [
          { stepId: 'aspect-requirement-fit', kind: 'aspect', model: 'm', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'pass' },
          { stepId: 'aspect-code-quality', kind: 'aspect', model: 'm', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'pass' },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    // The load effect fetches the raw step prompts; only code-quality recorded one.
    const httpMock = TestBed.inject(HttpTestingController);
    for (const req of httpMock.match(r => r.url.includes('/step-prompts'))) {
      req.flush({
        prompts: [
          { at: '2026-06-10T10:00:00.000Z', stepId: 'aspect-code-quality', templateRef: 'review/code-quality.md', model: 'claude-haiku-4-5', prompt: '# Review prompt\n\nGrade the code.' },
        ],
      });
    }
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    // Read-model lookup is case-insensitive on the step id.
    expect(c.stepPromptMarkdown('aspect-code-quality')).toContain('Grade the code.');
    expect(c.stepPromptMarkdown('ASPECT-CODE-QUALITY')).toContain('Grade the code.');
    // A step with no recorded prompt yields empty text so the trigger stays hidden.
    expect(c.stepPromptMarkdown('aspect-requirement-fit')).toBe('');

    c.expandAllPipelineGroups();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelectorAll('[data-testid="overview-pipeline-step-details"]').length).toBe(2);
  });

  it('pipeline block: run switcher shows archived attempts from pipeline history', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe = agentPipeline('claude-opus-4-8');
    pipe.pipeline.allSteps!.push({
      id: 'post-orchestrator-decision',
      displayName: 'Orchestrator decision',
      kind: 'orchestrator',
      runMode: 'sequential',
      dependsOn: [],
      idempotent: true,
      stub: false,
    });
    const run1Start = '2026-06-08T08:00:00.000Z';
    const run1Done = '2026-06-08T08:03:00.000Z';
    const run2Start = '2026-06-09T09:00:00.000Z';

    pipe.execution = {
      ...pipe.execution!,
      attempt: 2,
      startedAt: run2Start,
      completedAt: null,
      steps: [
        {
          stepId: 'core-agent-run',
          kind: 'core',
          model: 'claude-opus-4-8',
          status: 'running',
          startedAt: run2Start,
          completedAt: null,
          durationMs: 0,
          inputTokens: 0,
          outputTokens: 0,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
        },
      ],
      previousAttempts: [
        {
          pipelineId: 'standard-task-pipeline',
          pipelineVersion: 1,
          jobId: 'test-1',
          project: 'test',
          attempt: 1,
          startedAt: run1Start,
          completedAt: run1Done,
          previousAttempts: [],
          steps: [
            {
              stepId: 'core-agent-run',
              kind: 'core',
              model: 'claude-opus-4-8',
              status: 'passed',
              startedAt: run1Start,
              completedAt: run1Done,
              durationMs: 180_000,
              inputTokens: 10,
              outputTokens: 20,
              cacheReadTokens: 0,
              cacheCreationTokens: 0,
              tokenUsageSource: 'ARCHIVED ATTEMPT',
            },
            {
              stepId: 'aspect-code-quality',
              kind: 'aspect',
              model: 'claude-haiku-4-5',
              status: 'failed',
              startedAt: '2026-06-08T08:03:10.000Z',
              completedAt: '2026-06-08T08:04:00.000Z',
              durationMs: 50_000,
              inputTokens: 5,
              outputTokens: 7,
              cacheReadTokens: 0,
              cacheCreationTokens: 0,
              verdict: 'concerns',
            },
            {
              stepId: 'post-orchestrator-decision',
              kind: 'orchestrator',
              model: 'claude-haiku-4-5',
              status: 'failed',
              startedAt: '2026-06-08T08:04:00.000Z',
              completedAt: '2026-06-08T08:04:01.000Z',
              durationMs: 1_000,
              inputTokens: 5,
              outputTokens: 7,
              cacheReadTokens: 0,
              cacheCreationTokens: 0,
              verdict: 'escalate',
            },
          ],
        },
      ],
    };
    pipe.cost = emptyCost({
      steps: [
        stepCost({
          stepId: 'core-agent-run',
          kind: 'core',
          model: 'claude-opus-4-8',
          modelKnown: true,
          inputTokens: 99,
          outputTokens: 1,
          totalTokens: 100,
          costUsd: 0.5,
        }),
      ],
      totalInputTokens: 99,
      totalOutputTokens: 1,
      totalTokens: 100,
      totalCostUsd: 0.5,
    });
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    expect(c.pipelineRunOptions().map(r => r.attempt)).toEqual([2, 1]);
    expect(c.selectedPipelineAttemptNumber()).toBe(2);
    expect(c.selectedPipelineIsCurrent()).toBe(true);
    expect(c.pipelineRows().find(r => r.id === 'core-agent-run')!.status).toBe('running');
    expect(c.pipelineRows().find(r => r.id === 'core-agent-run')!.totalTokens).toBe(100);
    expect(c.pipelineTotal()?.totalTokens).toBe(100);

    const host = fixture.nativeElement as HTMLElement;
    c.expandAllPipelineGroups();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(host.querySelector('[data-testid="overview-post-step-actions"]')).not.toBeNull();
    const options = Array.from(
      host.querySelectorAll<HTMLButtonElement>('[data-testid="overview-pipeline-run-option"]'),
    );
    expect(options.length).toBe(2);
    expect(options[0].getAttribute('data-attempt')).toBe('2');
    expect(options[0].getAttribute('data-current')).toBe('true');
    expect(options[0].getAttribute('aria-selected')).toBe('true');

    const archivedRun = options.find(button => button.getAttribute('data-attempt') === '1')!;
    archivedRun.click();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    c.expandAllPipelineGroups();
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const archivedRows = c.pipelineRows();
    expect(c.selectedPipelineAttemptNumber()).toBe(1);
    expect(c.selectedPipelineIsCurrent()).toBe(false);
    expect(host.querySelector('[data-testid="overview-pipeline-superseded"]')?.textContent)
      .toContain('Attempt #1 · superseded');
    expect(host.querySelector('[data-testid="overview-pipeline-step-final-verdict"]')?.textContent)
      .toContain('Final verdict');
    expect(host.querySelector('[data-testid="overview-pipeline-step-verdict"]')?.textContent)
      .toContain('superseded');
    expect(
      host.querySelector('[data-step-id="post-orchestrator-decision"] .ov-pl-step__status')?.textContent?.trim(),
    ).toBe('×');
    expect(archivedRows.find(r => r.id === 'core-agent-run')!.status).toBe('passed');
    expect(archivedRows.find(r => r.id === 'core-agent-run')!.totalTokens).toBe(30);
    expect(archivedRows.find(r => r.id === 'aspect-code-quality')!.status).toBe('failed');
    expect(c.pipelineTotal()).toBeNull();
    expect(host.querySelector('[data-testid="overview-pipeline-total"]')).toBeNull();
    expect(
      host.querySelector('[data-step-id="core-agent-run"] [data-testid="overview-pipeline-step-tokens"]')
        ?.textContent
        ?.trim(),
    ).toBe('30');
    expect(
      host.querySelector('[data-testid="overview-pipeline-run-option"][aria-selected="true"]')
        ?.getAttribute('data-attempt'),
    ).toBe('1');
    expect(host.querySelector('[data-testid="overview-post-step-actions"]')).toBeNull();

    c.selectPipelineRun(2);
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(c.selectedPipelineAttemptNumber()).toBe(2);
    expect(c.selectedPipelineIsCurrent()).toBe(true);
    expect(host.querySelector('[data-testid="overview-post-step-actions"]')).not.toBeNull();
  });

  it('runs chip strip: collapses history past 8 chips behind a "+N more" toggle and expands by wrapping (ASS-1735)', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe = agentPipeline('claude-opus-4-8');
    const pad = (n: number) => String(n).padStart(2, '0');
    const archivedAttempt = (attempt: number) => ({
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: 'test-1',
      project: 'test',
      attempt,
      startedAt: `2026-06-09T${pad(attempt)}:00:00.000Z`,
      completedAt: `2026-06-09T${pad(attempt)}:05:00.000Z`,
      previousAttempts: [],
      steps: [
        {
          stepId: 'core-agent-run', kind: 'core' as const, model: 'claude-opus-4-8',
          status: 'passed' as const,
          startedAt: `2026-06-09T${pad(attempt)}:00:00.000Z`,
          completedAt: `2026-06-09T${pad(attempt)}:05:00.000Z`,
          durationMs: 300_000, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0,
        },
      ],
    });
    pipe.execution = {
      ...pipe.execution!,
      attempt: 13,
      startedAt: '2026-06-10T10:00:00.000Z',
      completedAt: null,
      steps: [
        {
          stepId: 'core-agent-run', kind: 'core', model: 'claude-opus-4-8', status: 'running',
          startedAt: '2026-06-10T10:00:00.000Z', completedAt: null, durationMs: 0,
          inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
        },
      ],
      // Newest-first: 12 archived runs (12 .. 1) trail the current run #13.
      previousAttempts: [12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1].map(archivedAttempt),
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    expect(c.pipelineRunOptions().length).toBe(13);
    expect(c.runSwitcherLimit()).toBe(8);
    // Current run is written out; 12 prior runs collapse to the most-recent 8.
    expect(c.historyRunOptions().length).toBe(12);
    expect(c.visibleHistoryChips().length).toBe(8);
    expect(c.hiddenRunCount()).toBe(4);
    expect(c.visibleHistoryChips().map(r => r.attempt)).toEqual([12, 11, 10, 9, 8, 7, 6, 5]);

    const host = fixture.nativeElement as HTMLElement;
    // One line: written-out current (1) + 8 collapsed chips = 9 run-options.
    expect(host.querySelectorAll('[data-testid="overview-pipeline-run-option"]').length).toBe(9);
    const more = host.querySelector<HTMLButtonElement>('[data-testid="overview-pipeline-run-more"]');
    expect(more).not.toBeNull();
    expect(more!.textContent?.trim()).toBe('+4 more');

    more!.click();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(c.runSwitcherExpanded()).toBe(true);
    expect(c.hiddenRunCount()).toBe(0);
    // Expanded: every run is a clickable option (wrapping, never a card grid).
    expect(host.querySelectorAll('[data-testid="overview-pipeline-run-option"]').length).toBe(13);
    expect(host.querySelector('[data-testid="overview-pipeline-run-more"]')).toBeNull();
    expect(host.querySelector('[data-testid="overview-pipeline-run-less"]')).not.toBeNull();

    host.querySelector<HTMLButtonElement>('[data-testid="overview-pipeline-run-less"]')!.click();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(c.runSwitcherExpanded()).toBe(false);
    expect(host.querySelectorAll('[data-testid="overview-pipeline-run-option"]').length).toBe(9);
  });

  it('runs chip strip: current run is written out; history reads as newest-first mini chips with result glyphs and a terse tooltip (ASS-1735)', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe = agentPipeline('claude-opus-4-8');
    pipe.execution = {
      ...pipe.execution!,
      attempt: 3,
      startedAt: '2026-06-10T10:00:00.000Z',
      completedAt: '2026-06-10T10:02:00.000Z',
      steps: [
        {
          stepId: 'core-agent-run', kind: 'core', model: 'claude-opus-4-8', status: 'passed',
          startedAt: '2026-06-10T10:00:00.000Z', completedAt: '2026-06-10T10:01:00.000Z', durationMs: 60_000,
          inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
        },
        {
          stepId: 'aspect-code-quality', kind: 'aspect', model: 'claude-haiku-4-5', status: 'passed',
          startedAt: '2026-06-10T10:01:00.000Z', completedAt: '2026-06-10T10:02:00.000Z', durationMs: 60_000,
          inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
        },
      ],
      previousAttempts: [
        {
          pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
          attempt: 2, startedAt: '2026-06-09T09:00:00.000Z', completedAt: '2026-06-09T09:03:34.000Z',
          previousAttempts: [],
          steps: [
            {
              stepId: 'core-agent-run', kind: 'core', model: 'claude-opus-4-8', status: 'passed',
              startedAt: '2026-06-09T09:00:00.000Z', completedAt: '2026-06-09T09:01:00.000Z', durationMs: 60_000,
              inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
            },
            {
              stepId: 'aspect-code-quality', kind: 'aspect', model: 'claude-haiku-4-5', status: 'failed',
              startedAt: '2026-06-09T09:01:00.000Z', completedAt: '2026-06-09T09:03:34.000Z', durationMs: 154_000,
              inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'concerns',
            },
          ],
        },
        {
          pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
          attempt: 1, startedAt: '2026-06-08T08:00:00.000Z', completedAt: '2026-06-08T08:03:00.000Z',
          previousAttempts: [],
          steps: [
            {
              stepId: 'core-agent-run', kind: 'core', model: 'claude-opus-4-8', status: 'passed',
              startedAt: '2026-06-08T08:00:00.000Z', completedAt: '2026-06-08T08:03:00.000Z', durationMs: 180_000,
              inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
            },
          ],
        },
      ],
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    // Current run first, prior runs newest-first.
    expect(c.pipelineRunOptions().map(o => o.attempt)).toEqual([3, 2, 1]);
    expect(c.historyRunOptions().map(o => o.attempt)).toEqual([2, 1]);

    const cur = c.currentRunOption()!;
    expect(cur.attempt).toBe(3);
    expect(cur.current).toBe(true);
    expect(cur.passed).toBe(2);
    expect(cur.failed).toBe(0);

    const [run2, run1] = c.historyRunOptions();
    // A run with any failure reads as ✗; an all-pass run reads as ✓.
    expect(run2.kind).toBe('fail');
    expect(run2.glyph).toBe('✗');
    expect(run1.kind).toBe('pass');
    expect(run1.glyph).toBe('✓');

    // Terse hover summary: outcome + duration; no token/cost detail (that lives
    // in the tokens-by-model section).
    expect(run2.tooltip.title).toBe('Attempt #2 · superseded');
    expect(run2.tooltip.body).toContain('1 fail');
    expect(run2.tooltip.body).toContain('3m 34s');
    expect(run2.tooltip.body).not.toContain('Tokens:');
    expect(run2.tooltip.body).not.toContain('Cost');

    const host = fixture.nativeElement as HTMLElement;
    // Current run is written out: dot, number, Current badge, OK counter, duration.
    const current = host.querySelector<HTMLButtonElement>(
      '[data-testid="overview-pipeline-run-option"][data-current="true"]',
    )!;
    expect(current.getAttribute('data-attempt')).toBe('3');
    expect(current.querySelector('[data-testid="overview-pipeline-run-current"]')?.textContent?.trim())
      .toBe('Current');
    expect(current.textContent).toContain('#3');
    expect(current.textContent).toContain('2 OK');
    expect(current.textContent).toContain('2m');

    // History renders as neutral, explicit superseded attempts. Old result
    // colours/glyphs must not read as current state.
    const chips = Array.from(
      host.querySelectorAll<HTMLButtonElement>(
        '[data-testid="overview-pipeline-run-option"]:not([data-current="true"])',
      ),
    );
    expect(chips.map(b => b.getAttribute('data-attempt'))).toEqual(['2', '1']);
    expect(chips[0].textContent?.replace(/\s+/g, '')).toBe('Attempt#2superseded');
    expect(chips[1].textContent?.replace(/\s+/g, '')).toBe('Attempt#1superseded');
    expect(chips[0].getAttribute('data-kind')).toBeNull();
    expect(chips[1].getAttribute('data-kind')).toBeNull();

    // The current run is selected by default; clicking a chip swaps the detail.
    expect(c.selectedPipelineAttemptNumber()).toBe(3);
    expect(c.selectedPipelineIsCurrent()).toBe(true);
    chips[0].click();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(c.selectedPipelineAttemptNumber()).toBe(2);
    expect(c.selectedPipelineIsCurrent()).toBe(false);
    expect(
      host.querySelector('[data-testid="overview-pipeline-run-option"][aria-selected="true"]')
        ?.getAttribute('data-attempt'),
    ).toBe('2');
  });

  it('pipeline block: CORE CLI-footer tokens render with source, API-price tooltip, run count, and SUM footer', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe = agentPipeline('claude-opus-4-8');
    pipe.execution!.steps[0] = {
      ...pipe.execution!.steps[0],
      status: 'passed',
      durationMs: 125_000,
      inputTokens: 2_500,
      outputTokens: 195_600,
      cacheReadTokens: 18_500_000,
      cacheCreationTokens: 1_000_000,
      tokenUsageSource: 'AGENT (CLI FOOTER) / reported',
    };
    pipe.cost = emptyCost({
      steps: [
        stepCost({
          stepId: 'core-agent-run',
          kind: 'core',
          model: 'claude-opus-4-8',
          tokenUsageSource: 'AGENT (CLI FOOTER) / reported',
          modelKnown: true,
          inputTokens: 2_500,
          outputTokens: 195_600,
          cacheReadTokens: 18_500_000,
          cacheCreationTokens: 1_000_000,
          totalTokens: 19_698_100,
          inputCostUsd: 0.0125,
          outputCostUsd: 4.89,
          cacheReadCostUsd: 9.25,
          cacheCreationCostUsd: 6.25,
          costUsd: 20.4025,
        }),
      ],
      totalInputTokens: 2_500,
      totalOutputTokens: 195_600,
      totalCacheReadTokens: 18_500_000,
      totalCacheCreationTokens: 1_000_000,
      totalTokens: 19_698_100,
      totalInputCostUsd: 0.0125,
      totalOutputCostUsd: 4.89,
      totalCacheReadCostUsd: 9.25,
      totalCacheCreationCostUsd: 6.25,
      totalCostUsd: 20.4025,
    });
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    TestBed.inject(RunTimelinePollService).timeline.set(
      runTimeline(8, [runRecord({ durationSeconds: 10 })]),
    );

    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const core = c.pipelineRows().find(r => r.id === 'core-agent-run')!;
    expect(core.totalTokens).toBe(19_698_100);
    expect(core.tokenUsageSource).toBe('AGENT (CLI FOOTER) / reported');
    expect(core.tokenTooltip?.body).toContain('Source: AGENT (CLI FOOTER) / reported');
    expect(core.tokenTooltip?.body).toContain('Input: 2.5k');
    expect(core.tokenTooltip?.body).toContain('Output: 195.6k');
    expect(core.tokenTooltip?.body).toContain('Cache read: 18.5m');
    expect(core.tokenTooltip?.body).toContain('Cache creation: 1m');
    expect(core.tokenTooltip?.body).toContain('Estimated cost: $20.40');
    expect(core.tokenTooltip?.body).toContain('historical list prices');
    expect(core.tokenTooltip?.body).toContain('discounts and provider-side caching adjustments are not considered');
    expect(c.agentRunCountLabel()).toBe('8 runs');
    expect(c.pipelineTotal()?.tokenTooltip?.title).toBe('Task total tokens (SUM)');
    expect(c.pipelineTotal()?.tokenTooltip?.body).toContain('Source: SUM of pipeline steps');

    c.expandAllPipelineGroups();
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="overview-pipeline-step-tokens"]')?.textContent?.trim()).toBe('19.7m');
    expect(el.querySelector('[data-testid="overview-pipeline-agent-runs"]')?.textContent?.trim()).toBe('8 runs');
    expect(el.querySelector('.ov-pl-total__label')?.textContent).toContain('SUM');
  });

  it('pipeline block: missing and mixed prices never render as a silent zero', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe = agentPipeline('gpt-5.6-sol');
    pipe.execution!.steps.push({
      stepId: 'aspect-code-quality', kind: 'aspect', model: 'claude-haiku-4-5',
      status: 'passed', startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
      durationMs: 10, inputTokens: 1_000_000, outputTokens: 200_000,
      cacheReadTokens: 0, cacheCreationTokens: 0,
    });
    const gap = { modelId: 'gpt-5.6-sol', reason: 'NoPriceForDate', affectedRuns: 1 };
    pipe.cost = emptyCost({
      steps: [
        stepCost({
          stepId: 'core-agent-run', kind: 'core', model: 'gpt-5.6-sol', modelKnown: false,
          inputTokens: 500_000, outputTokens: 100_000, totalTokens: 600_000,
          pricingGaps: [gap],
        }),
        stepCost({
          stepId: 'aspect-code-quality', kind: 'aspect', model: 'claude-haiku-4-5', modelKnown: true,
          inputTokens: 1_000_000, outputTokens: 200_000, totalTokens: 1_200_000,
          inputCostUsd: 1, outputCostUsd: 1, costUsd: 2,
        }),
      ],
      totalInputTokens: 1_500_000,
      totalOutputTokens: 300_000,
      totalTokens: 1_800_000,
      totalInputCostUsd: 1,
      totalOutputCostUsd: 1,
      totalCostUsd: 2,
      anyModelUnknown: true,
      unpricedRuns: 1,
      pricingGaps: [gap],
    });
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    fixture.componentInstance.expandAllPipelineGroups();
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const host = fixture.nativeElement as HTMLElement;
    const coreCost = host.querySelector(
      '[data-step-id="core-agent-run"] [data-testid="overview-pipeline-step-cost"]',
    );
    expect(coreCost?.textContent).toContain('no price data');
    expect(coreCost?.textContent).not.toContain('$0.00');

    const total = host.querySelector('[data-testid="overview-pipeline-total-cost"]');
    expect(total?.textContent).toContain('$2.00');
    expect(total?.textContent).toContain('incomplete (1 run without price)');
    expect(fixture.componentInstance.pipelineTotal()?.costTooltip?.body).toContain('gpt-5.6-sol');
    expect(fixture.componentInstance.pipelineTotal()?.costTooltip?.body).toContain('NoPriceForDate');

    const coreTokens = host.querySelector<HTMLButtonElement>(
      '[data-step-id="core-agent-run"] [data-testid="overview-pipeline-step-tokens"]',
    );
    coreTokens?.click();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    const modal = document.body.querySelector<HTMLElement>('[data-testid="overview-step-token-modal"]');
    expect(modal?.textContent).toContain('Calls');
    const modalCost = modal?.querySelector('[data-testid="overview-step-token-modal-cost"]');
    const modalTotalCost = modal?.querySelector('[data-testid="overview-step-token-modal-total-cost"]');
    expect(modalCost?.textContent).toContain('- no price data');
    expect(modalTotalCost?.textContent).toContain('- no price data');
    expect(modalCost?.textContent).not.toContain('$0.00');
  });

  it('pipeline block: clicking CORE tokens opens a step-scoped token modal', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const startedAt = new Date('2026-06-09T08:00:00.000Z').toISOString();
    const completedAt = new Date('2026-06-09T08:02:05.000Z').toISOString();
    const pipe = agentPipeline('claude-opus-4-8');
    pipe.execution!.steps[0] = {
      ...pipe.execution!.steps[0],
      status: 'passed',
      startedAt,
      completedAt,
      durationMs: 125_000,
      inputTokens: 2_500,
      outputTokens: 195_600,
      cacheReadTokens: 18_500_000,
      cacheCreationTokens: 1_000_000,
      tokenUsageSource: 'AGENT (CLI FOOTER) / reported',
    };
    pipe.cost = emptyCost({
      steps: [
        stepCost({
          stepId: 'core-agent-run',
          kind: 'core',
          model: 'claude-opus-4-8',
          tokenUsageSource: 'AGENT (CLI FOOTER) / reported',
          modelKnown: true,
          inputTokens: 2_500,
          outputTokens: 195_600,
          cacheReadTokens: 18_500_000,
          cacheCreationTokens: 1_000_000,
          totalTokens: 19_698_100,
          inputCostUsd: 0.0125,
          outputCostUsd: 4.89,
          cacheReadCostUsd: 9.25,
          cacheCreationCostUsd: 6.25,
          costUsd: 20.4025,
        }),
      ],
      totalInputTokens: 2_500,
      totalOutputTokens: 195_600,
      totalCacheReadTokens: 18_500_000,
      totalCacheCreationTokens: 1_000_000,
      totalTokens: 19_698_100,
      totalInputCostUsd: 0.0125,
      totalOutputCostUsd: 4.89,
      totalCacheReadCostUsd: 9.25,
      totalCacheCreationCostUsd: 6.25,
      totalCostUsd: 20.4025,
    });
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    TestBed.inject(RunTimelinePollService).timeline.set(runTimeline(2, [
      runRecord({ startedAt, endedAt: completedAt, durationSeconds: 125 }),
    ]));

    try { fixture.detectChanges(); } catch { /* ignore */ }

    fixture.componentInstance.expandAllPipelineGroups();
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="overview-tokens"]')).toBeNull();
    const tokenCell = host.querySelector<HTMLButtonElement>(
      '[data-step-id="core-agent-run"] [data-testid="overview-pipeline-step-tokens"]',
    );
    expect(tokenCell).not.toBeNull();
    tokenCell!.click();
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const modal = document.body.querySelector<HTMLElement>('[data-testid="overview-step-token-modal"]');
    expect(modal).not.toBeNull();
    const text = modal!.textContent ?? '';
    expect(text).toContain('Agent execution');
    expect(text).toContain('AGENT (CLI FOOTER) / reported');
    expect(text).toContain('2 agent runs');
    expect(text).toContain('$20.40');
    expect(text).toContain('Input');
    expect(text).toContain('2.5k');
    expect(text).toContain('Output');
    expect(text).toContain('195.6k');
    expect(text).toContain('Cache read');
    expect(text).toContain('18.5m');
    expect(text).toContain('Cache write');
    expect(text).toContain('1m');
    expect(text).toContain('Total');
    expect(text).toContain('19.7m');
    expect(text).toContain('CORE totals come from the agent CLI footer');
    expect(modal!.querySelector('[data-testid="overview-step-token-modal-total-note"]')).toBeNull();

    fixture.componentInstance.closeStepTokenModal();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(document.body.querySelector('[data-testid="overview-step-token-modal"]')).toBeNull();
  });

  it('pipeline block: an in-flight execution row surfaces a running status the template can highlight', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
          { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
          { id: 'post-orchestrator-decision', displayName: 'Final verdict', kind: 'orchestrator', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: null,
        steps: [
          { stepId: 'core-agent-run', kind: 'core', model: 'claude-opus-4-7', status: 'running', startedAt: new Date().toISOString(), completedAt: null, durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const rows = c.pipelineRows();
    const core = rows.find(r => r.id === 'core-agent-run')!;
    expect(core.status).toBe('running');
    // A step the runtime has not reached yet stays pending, so only the one
    // active step lights up.
    const cq = rows.find(r => r.id === 'aspect-code-quality')!;
    expect(cq.status).toBe('pending');
    expect(c.stepStatusLabel('running')).toBe('Running');
  });

  it('pipeline block: a settled escalation attempt labels untouched full-pipeline steps as not run', async () => {
    const fixture = await build(baseJob({
      state: TaskState.Escalated,
      orchestratorVerdict: 'escalate',
    }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'pre-loop-guard', displayName: 'Loop check', kind: 'module', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
          { id: 'post-orchestrator-decision', displayName: 'Decision', kind: 'orchestrator', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
          { id: 'post-merge-into-develop', displayName: 'Merge', kind: 'tool', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false, deferred: true },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: '2026-07-29T10:00:00Z', completedAt: null, attempt: 3,
        previousAttempts: [
          { pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test', startedAt: '2026-07-27T10:00:00Z', completedAt: '2026-07-27T10:02:00Z', attempt: 2, steps: [] },
          { pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test', startedAt: '2026-07-25T10:00:00Z', completedAt: '2026-07-25T10:02:00Z', attempt: 1, steps: [] },
        ],
        steps: [
          { stepId: 'pre-loop-guard', kind: 'module', status: 'pending', durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
          { stepId: 'core-agent-run', kind: 'core', status: 'pending', durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
          { stepId: 'post-orchestrator-decision', kind: 'orchestrator', status: 'pending', durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
          { stepId: 'post-merge-into-develop', kind: 'tool', status: 'pending', durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const rows = c.pipelineRows();
    expect(rows.filter(row => row.id !== 'post-merge-into-develop').every(row => row.status === 'not-run')).toBe(true);
    expect(rows.find(row => row.id === 'core-agent-run')?.skipHint)
      .toBe('not run: lightweight pipeline or escalation');
    expect(rows.find(row => row.id === 'post-merge-into-develop')?.status).toBe('pending');
    expect(c.pipelineGroups().find(group => group.phaseKey === 'core')?.tone).toBe('not-run');
    expect(c.groupToneLabel('not-run')).toBe('Not run');
    const currentRun = fixture.nativeElement.querySelector(
      '[data-testid="overview-pipeline-run-option"][data-current="true"]',
    ) as HTMLElement | null;
    expect(currentRun?.textContent).toContain('not run');
  });

  it('pipeline block: remote-only skipped steps say not applicable instead of not run', async () => {
    const fixture = await build(baseJob({ state: TaskState.Completed }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
          { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
          { id: 'post-orchestrator-decision', displayName: 'Final verdict', kind: 'orchestrator', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: '2026-07-30T08:00:00Z', completedAt: '2026-07-30T08:20:00Z',
        steps: [
          {
            stepId: 'core-agent-run', kind: 'core', status: 'passed',
            startedAt: '2026-07-30T08:00:00Z', completedAt: '2026-07-30T08:10:00Z',
            durationMs: 600_000, inputTokens: 100, outputTokens: 20,
            cacheReadTokens: 0, cacheCreationTokens: 0,
          },
          {
            stepId: 'aspect-code-quality', kind: 'aspect', status: 'skipped',
            durationMs: 0, inputTokens: 0, outputTokens: 0,
            cacheReadTokens: 0, cacheCreationTokens: 0,
            reason: 'Executed remotely; this local pipeline step is not applicable.',
          },
          {
            stepId: 'post-orchestrator-decision', kind: 'orchestrator', status: 'passed',
            durationMs: 0, inputTokens: 10, outputTokens: 2,
            cacheReadTokens: 0, cacheCreationTokens: 0,
            verdict: 'pass',
            reason: 'Remote Review Plane verdict Pass (attempt rat-2427).',
          },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    fixture.componentRef.setInput('runOutcome', {
      kind: 'task-state', status: 'succeeded', emoji: '🟢', label: 'Pipeline completed',
      detail: 'The task is delivered.', duration: null, signals: [],
    });
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const aspect = fixture.componentInstance.pipelineRows()
      .find(row => row.id === 'aspect-code-quality');
    expect(aspect?.status).toBe('notApplicable');
    expect(aspect?.skipHint).toBe('executed remotely · not applicable');
    expect(aspect?.statusTooltip?.body).toContain('Executed remotely');
    const decision = fixture.componentInstance.pipelineRows()
      .find(row => row.id === 'post-orchestrator-decision');
    expect(fixture.componentInstance.decisionBadgeForRow(decision!)?.verdict).toBe('pass');
    expect(fixture.componentInstance.decisionBadgeForRow(decision!)?.label).toBe('Review Pass');

    fixture.componentInstance.expandAllPipelineGroups();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    const row = fixture.nativeElement.querySelector(
      '[data-step-id="aspect-code-quality"]',
    ) as HTMLElement | null;
    expect(row?.getAttribute('data-status')).toBe('notApplicable');
    expect(row?.textContent).toContain('executed remotely · not applicable');
    expect(row?.textContent).not.toContain('not run:');
  });

  it('pipeline block: no verify commands is neutral while a required skipped build gate needs attention', async () => {
    const fixture = await build(baseJob({ state: '5-human-review' }));
    const poll = TestBed.inject(TaskPipelinePollService);
    const response = (status: 'notApplicable' | 'skipped', reason: string): TaskPipelineResponse => ({
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'post-build-test-gate', displayName: 'Build/test gate', kind: 'tool', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: '2026-08-08T10:00:00Z', completedAt: '2026-08-08T10:00:01Z',
        steps: [{
          stepId: 'post-build-test-gate', kind: 'tool', model: null, status,
          startedAt: '2026-08-08T10:00:00Z', completedAt: '2026-08-08T10:00:01Z', durationMs: 1,
          inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
          verdict: status === 'notApplicable' ? 'not-applicable' : 'skipped', reason,
        }],
      },
      cost: emptyCost(),
      config: {},
    });

    // Legacy AOW records persisted Skipped before NotApplicable existed. The
    // exact old reason is projected into the new neutral class at read time.
    poll.pipeline.set(response('skipped', 'no verify commands derivable'));
    fixture.componentInstance.expandAllPipelineGroups();
    fixture.detectChanges();
    let row = fixture.componentInstance.pipelineRows()[0];
    expect(row.status).toBe('notApplicable');
    expect(row.skipHint).toBe('no build/test defined');
    expect(row.attentionRequired).toBe(false);
    expect(row.statusTooltip).toEqual({
      title: 'Build/test gate: Not applicable',
      body: 'no verify commands derivable',
    });
    expect(fixture.nativeElement.querySelector('[data-step-id="post-build-test-gate"]')?.getAttribute('data-status'))
      .toBe('notApplicable');

    poll.pipeline.set(response('skipped', 'pipeline interrupted before command execution'));
    fixture.detectChanges();
    row = fixture.componentInstance.pipelineRows()[0];
    expect(row.status).toBe('skipped');
    expect(row.attentionRequired).toBe(true);
    expect(row.statusTooltip).toEqual({
      title: 'Build/test gate: Skipped',
      body: 'pipeline interrupted before command execution',
    });
    expect(fixture.nativeElement.querySelector('[data-step-id="post-build-test-gate"]')?.getAttribute('data-attention-required'))
      .toBe('true');
  });

  it('pipeline block: subset passes plus failed and skipped statuses expose their recorded reasons', async () => {
    const fixture = await build(baseJob({ state: '5-human-review' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'post-orchestrator-review', displayName: 'Early completeness gate', kind: 'orchestrator', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
          { id: 'post-build-test-gate', displayName: 'Build/test gate', kind: 'tool', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
          { id: 'post-subset-build-test-gate', displayName: 'Subset build/test gate', kind: 'tool', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
          { id: 'post-wiki-maintenance', displayName: 'Wiki maintenance', kind: 'tool', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: '2026-07-12T04:47:20Z', completedAt: '2026-07-12T05:00:18Z',
        steps: [
          {
            stepId: 'post-orchestrator-review', kind: 'orchestrator', model: null, status: 'failed',
            startedAt: '2026-07-12T04:47:20Z', completedAt: '2026-07-12T04:47:21Z', durationMs: 1_000,
            inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
            verdict: 'escalate', reason: 'Attempt budget exhausted at the early completeness gate.',
          },
          {
            stepId: 'post-build-test-gate', kind: 'tool', model: null, status: 'failed',
            startedAt: '2026-07-12T04:47:20Z', completedAt: '2026-07-12T05:00:18Z', durationMs: 778_000,
            inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
            verdict: 'fail', reason: '`dotnet test --filter Category!=MachineBound --nologo` exit 1',
          },
          {
            stepId: 'post-subset-build-test-gate', kind: 'tool', model: null, status: 'passed',
            startedAt: '2026-07-12T04:47:20Z', completedAt: '2026-07-12T04:48:18Z', durationMs: 58_000,
            inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
            verdict: 'ok',
            reason: 'verify gate passed; test-level=work-package; selected=2; full-suite=not-run; omitted=11',
          },
          {
            stepId: 'post-wiki-maintenance', kind: 'tool', model: null, status: 'skipped',
            startedAt: '2026-07-12T05:00:18Z', completedAt: '2026-07-12T05:00:18Z', durationMs: 0,
            inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
            verdict: 'not-needed', verdictSummary: 'No matching wiki topic was configured.',
          },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const rows = fixture.componentInstance.pipelineRows();
    expect(rows.find(row => row.id === 'post-build-test-gate')?.statusTooltip).toEqual({
      title: 'Build/test gate: Failed',
      body: '`dotnet test --filter Category!=MachineBound --nologo` exit 1',
    });
    expect(rows.find(row => row.id === 'post-subset-build-test-gate')?.statusTooltip).toEqual({
      title: 'Subset build/test gate: Passed',
      body: 'verify gate passed; test-level=work-package; selected=2; full-suite=not-run; omitted=11',
    });
    expect(rows.find(row => row.id === 'post-wiki-maintenance')?.statusTooltip).toEqual({
      title: 'Wiki maintenance: Skipped',
      body: 'No matching wiki topic was configured.',
    });
    expect(rows.find(row => row.id === 'post-wiki-maintenance')?.skipHint)
      .toBe('skipped: chain ended by early escalate');

    fixture.componentInstance.expandAllPipelineGroups();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="overview-pipeline-skip-hint"]')?.textContent)
      .toContain('skipped: chain ended by early escalate');
  });

  it('pipeline block: per-step rows carry start/end stamps and a live-counting duration for the running step', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    const runningStart = new Date(Date.now() - 4_000).toISOString();
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
          { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: '2026-06-02T08:00:00Z', completedAt: null,
        steps: [
          { stepId: 'aspect-code-quality', kind: 'aspect', model: 'm', status: 'passed', startedAt: '2026-06-02T08:00:00Z', completedAt: '2026-06-02T08:00:42Z', durationMs: 42_000, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'pass' },
          { stepId: 'core-agent-run', kind: 'core', model: 'm', status: 'running', startedAt: runningStart, completedAt: null, durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const rows = c.pipelineRows();
    const done = rows.find(r => r.id === 'aspect-code-quality')!;
    const running = rows.find(r => r.id === 'core-agent-run')!;
    const pending = rows.find(r => r.id === 'aspect-code-quality');

    // Rows carry the raw stamps so the table can render times.
    expect(done.startedAt).toBe('2026-06-02T08:00:00Z');
    expect(done.completedAt).toBe('2026-06-02T08:00:42Z');
    expect(running.startedAt).toBe(runningStart);
    expect(running.completedAt).toBeNull();

    // A completed step shows its recorded duration, independent of the clock.
    expect(c.liveStepDurationMs(done)).toBe(42_000);
    // The running step counts up from its start (≈ now − startedAt), so the
    // value is the live elapsed time, not the recorded 0.
    const live = c.liveStepDurationMs(running);
    expect(live).toBeGreaterThanOrEqual(3_000);
    expect(live).toBeLessThan(60_000);

    // Wall-clock formatter is locale-tolerant: HH:MM, empty for unset.
    expect(c.formatClock(done.startedAt)).toMatch(/\d{1,2}:\d{2}/);
    expect(c.formatClock(null)).toBe('');
    expect(c.formatClock('not-a-date')).toBe('');

    // Timing tooltip: start + end + duration for a finished step; a live
    // "running for" line while in flight; null before the step starts.
    const doneTip = c.stepTimingTooltip(done)!;
    expect(doneTip.title).toBe('Code quality');
    expect(doneTip.body).toContain('Started:');
    expect(doneTip.body).toContain('Ended:');
    expect(doneTip.body).toContain('Duration:');

    const runTip = c.stepTimingTooltip(running)!;
    expect(runTip.body).toContain('Running for');
    expect(runTip.body).not.toContain('Ended:');

    expect(pending).toBeTruthy();
    // A row with no start stamp carries no timing tooltip.
    const noStart = { ...done, startedAt: null, completedAt: null, status: 'pending' as const };
    expect(c.stepTimingTooltip(noStart)).toBeNull();
  });

  it('pipeline block: concern tooltip is built for a non-pass aspect verdict, and absent for pass', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'aspect-requirement-fit', displayName: 'Requirement fit', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
          { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
        steps: [
          { stepId: 'aspect-requirement-fit', kind: 'aspect', model: 'm', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'concerns', verdictSummary: 'Acceptance item 3 (empty-state tooltip) has no evidence.' },
          { stepId: 'aspect-code-quality', kind: 'aspect', model: 'm', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'pass' },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const rows = fixture.componentInstance.pipelineRows();
    const concerned = rows.find(r => r.id === 'aspect-requirement-fit')!;
    expect(concerned.verdict).toBe('concerns');
    expect(concerned.concernTooltip).not.toBeNull();
    expect(concerned.concernTooltip!.title).toBe('Requirement fit · Concerns');
    expect(concerned.concernTooltip!.body).toContain('Acceptance item 3');

    // A pass verdict (or a verdict with no summary) must not grow a tooltip.
    const passing = rows.find(r => r.id === 'aspect-code-quality')!;
    expect(passing.verdict).toBe('pass');
    expect(passing.concernTooltip).toBeNull();
  });

  it('pipeline block: model qualification renders selected model, reasoning, and override explanation', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    TestBed.inject(TaskPipelinePollService).pipeline.set({
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'pre-model-qualification', displayName: 'Model qualification', kind: 'module', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: null,
        steps: [{
          stepId: 'pre-model-qualification', kind: 'module', model: 'gpt-balanced', thinkingLevel: 'medium',
          recommendedModel: 'gpt-economy', recommendedThinkingLevel: 'low', selectionSource: 'task-override',
          estimatedSavingsPercent: 0, status: 'passed', durationMs: 2,
          inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
          verdict: 'override', verdictSummary: 'chore/small/frontend polish; card override wins, selected gpt-balanced at medium',
        }],
      },
      cost: emptyCost(),
      config: {},
    });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const row = fixture.componentInstance.pipelineRows()[0];
    expect(row.model).toBe('gpt-balanced');
    expect(row.thinkingLevel).toBe('medium');
    expect(row.concernTooltip?.title).toBe('Model qualification · Card override');
    expect(row.concernTooltip?.body).toContain('card override wins');
  });

  it('pipeline block: a circuit-broken loop guard surfaces a loop-detected verdict as the first row with a concern tooltip', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'pre-loop-guard', displayName: 'Loop check', kind: 'module', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: null,
        steps: [
          { stepId: 'pre-loop-guard', kind: 'module', status: 'failed', durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'loop-detected', verdictSummary: 'Auto-loop circuit-breaker fired after 5/5 iterations; awaiting user.' },
          { stepId: 'core-agent-run', kind: 'core', status: 'passed', durationMs: 1, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const rows = c.pipelineRows();
    // The loop guard is the first row (pre steps), so a detected loop is visible early.
    expect(rows[0].id).toBe('pre-loop-guard');
    expect(c.stepKindLabel(rows[0].kind)).toBe('Pre steps');
    expect(c.stepKindIcon(rows[0].kind)).toBe('sliders');
    expect(c.stepKindIcon('core')).toBe('bot');
    expect(c.stepKindIcon('aspect')).toBe('eye');
    expect(c.stepKindIcon('orchestrator')).toBe('branch');
    expect(c.stepKindIcon('tool')).toBe('cli');
    expect(c.stepKindIcon('drift')).toBe('diff');

    const guard = rows.find(r => r.id === 'pre-loop-guard')!;
    expect(guard.status).toBe('failed');
    expect(guard.verdict).toBe('loop-detected');
    expect(guard.concernTooltip).not.toBeNull();
    expect(guard.concernTooltip!.title).toBe('Loop check · Loop detected');
    expect(guard.concernTooltip!.body).toContain('circuit-breaker');
  });

  it('pipeline block: a failed CORE step that still claims a success verdict drops the contradictory badge (ASS-2)', async () => {
    // Bug ASS-2: a legacy on-disk record persisted the CORE step with
    // status='failed' (red ❌ icon) AND verdict='success' (green SUCCESS badge)
    // at once. The status is authoritative, so the success badge is suppressed
    // and the row tells one story.
    const fixture = await build(baseJob({ state: '3-progress' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
        steps: [
          { stepId: 'core-agent-run', kind: 'core', status: 'failed', durationMs: 1, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'success' },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const core = c.pipelineRows().find(r => r.id === 'core-agent-run')!;
    // Icon stays Failed; the contradictory success verdict is reconciled away.
    expect(core.status).toBe('failed');
    expect(c.stepStatusIcon(core.status)).toBe('❌');
    expect(core.verdict).toBeNull();

    // And the DOM carries no SUCCESS badge on the core row.
    const coreRow = fixture.nativeElement.querySelector('[data-step-id="core-agent-run"]');
    expect(coreRow).not.toBeNull();
    expect(coreRow.querySelector('[data-testid="overview-pipeline-step-verdict"]')).toBeNull();
  });

  it('pipeline block: a passed CORE step keeps its success verdict (consistent record is untouched)', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
        steps: [
          { stepId: 'core-agent-run', kind: 'core', status: 'passed', durationMs: 1, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'success' },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const core = fixture.componentInstance.pipelineRows().find(r => r.id === 'core-agent-run')!;
    expect(core.status).toBe('passed');
    expect(core.verdict).toBe('success');
  });

  it('pipeline block: a forming loop under budget reads as a passed guard with a looping verdict', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'pre-loop-guard', displayName: 'Loop check', kind: 'module', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: null,
        steps: [
          { stepId: 'pre-loop-guard', kind: 'module', status: 'passed', durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'looping', verdictSummary: 'Auto-mode loop forming: 2/5 iterations, 40000/200000 orchestrator tokens used.' },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const guard = fixture.componentInstance.pipelineRows().find(r => r.id === 'pre-loop-guard')!;
    expect(guard.status).toBe('passed');
    expect(guard.verdict).toBe('looping');
    expect(guard.concernTooltip).not.toBeNull();
    expect(guard.concernTooltip!.title).toBe('Loop check · Loop forming');
  });

  it('pipeline block: parallel aspects carry a muted parallel note and the orchestrator decision renders as a separate final-verdict step', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'aspect-requirement-fit', displayName: 'Requirement fit', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
          { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
          { id: 'post-orchestrator-decision', displayName: 'Final verdict', kind: 'orchestrator', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
        steps: [
          { stepId: 'aspect-requirement-fit', kind: 'aspect', model: 'm', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'pass' },
          { stepId: 'aspect-code-quality', kind: 'aspect', model: 'm', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'pass' },
          { stepId: 'post-orchestrator-decision', kind: 'orchestrator', model: 'm', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'accept', verdictSummary: 'All aspects passed; accepting.' },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    fixture.componentRef.setInput('runOutcome', {
      kind: 'problem', status: 'failed', emoji: '🔴', label: 'Watchdog timeout',
      detail: 'The run will finalize as failed.', duration: null,
      signals: [],
    });
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const rows = c.pipelineRows();

    // Aspect rows are flagged parallel so the template can mark them quietly.
    const aspects = rows.filter(r => r.kind === 'aspect');
    expect(aspects.length).toBe(2);
    expect(aspects.every(r => r.runMode === 'parallel')).toBe(true);

    // The orchestrator decision is its own, separate row (Req 3).
    const decision = rows.find(r => r.id === 'post-orchestrator-decision')!;
    expect(decision.kind).toBe('orchestrator');
    expect(decision.runMode).toBe('sequential');
    expect(decision.verdict).toBe('accept');
    expect(c.stepKindLabel(decision.kind)).toBe('Decision');

    c.expandAllPipelineGroups();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    // The DOM carries muted parallel metadata on aspect rows and exactly one
    // final-verdict chip on the orchestrator row.
    const parallelNotes = fixture.nativeElement.querySelectorAll('[data-testid="overview-pipeline-step-parallel"]');
    expect(parallelNotes.length).toBe(2);
    expect(parallelNotes[0].textContent?.trim()).toBe('∥');
    expect(parallelNotes[0].getAttribute('aria-label')).toBe('Parallel review pool');
    expect(parallelNotes[0].classList.contains('ov-pl-step__parallel-note')).toBe(true);
    const finalChips = fixture.nativeElement.querySelectorAll('[data-testid="overview-pipeline-step-final-verdict"]');
    expect(finalChips.length).toBe(1);

    // The decision row is marked so the template can visually separate it.
    const decisionEl = fixture.nativeElement.querySelector('[data-step-id="post-orchestrator-decision"]') as HTMLElement | null;
    expect(decisionEl).not.toBeNull();
    expect(decisionEl!.classList.contains('ov-pl-step--final-verdict')).toBe(true);
    expect(decisionEl!.getAttribute('data-run-mode')).toBe('sequential');
    const authoritative = decisionEl!.querySelector('[data-testid="overview-pipeline-step-final-verdict"]');
    expect(authoritative?.textContent).toContain('Watchdog timeout');
    expect(authoritative?.getAttribute('data-verdict')).toBe('failed');
  });

  it('decision badge: only the final ruling projects the latest steering verdict, with reasoning in the tooltip (ASS-1706)', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'post-orchestrator-review', displayName: 'Post-Core Orchestrator-Review', kind: 'orchestrator', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
          { id: 'post-orchestrator-decision', displayName: 'Final Orchestrator-Review', kind: 'orchestrator', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
        steps: [
          { stepId: 'post-orchestrator-review', kind: 'orchestrator', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'complete' },
          { stepId: 'post-orchestrator-decision', kind: 'orchestrator', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'accept' },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);

    const steerEvent: TaskTimelineEvent = {
      ts: new Date().toISOString(),
      kind: 'quality_loop_reopened',
      actor: 'orchestrator',
      summary: 'Reopened for missing tests.',
      details: { gap: 'requirement-fit still open', attempt: '2', maxAttempts: '5' },
    };
    TestBed.inject(TaskTimelinePollService).events.set([steerEvent]);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const rows = c.pipelineRows();
    const decision = rows.find(r => r.id === 'post-orchestrator-decision')!;
    const review = rows.find(r => r.id === 'post-orchestrator-review')!;

    // The final ruling projects the steering verdict into a compact badge.
    const badge = c.decisionBadgeForRow(decision);
    expect(badge).not.toBeNull();
    expect(badge!.verdict).toBe('reissue');
    expect(badge!.label).toBe('Re-issue');
    expect(badge!.tone).toBe('warn');
    expect(badge!.severity).toBe('warn');
    // The detailed reasoning + context lives in the tooltip, not inline.
    expect(badge!.tooltip.title).toBe('Decision · Re-issue');
    expect(badge!.tooltip.body).toContain('requirement-fit still open');
    expect(badge!.tooltip.body).toContain('Attempt: 2 / 5');

    // The early gate is NOT the final ruling -> no badge, keeps its own pill.
    expect(c.decisionBadgeForRow(review)).toBeNull();
  });

  it('decision badge: the final ruling falls back to its generic verdict pill when no steering event exists', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'post-orchestrator-decision', displayName: 'Final Orchestrator-Review', kind: 'orchestrator', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
        steps: [
          { stepId: 'post-orchestrator-decision', kind: 'orchestrator', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'accept' },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const decision = c.pipelineRows().find(r => r.id === 'post-orchestrator-decision')!;
    expect(c.decisionBadgeForRow(decision)?.label).toBe('accept');

    c.expandAllPipelineGroups();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    // With no badge, the generic verdict pill renders the recorded verdict.
    const verdictEl = fixture.nativeElement.querySelector('[data-step-id="post-orchestrator-decision"] [data-testid="overview-pipeline-step-final-verdict"]');
    expect(verdictEl?.textContent).toContain('Final verdict → accept');
  });

  it('pipeline block: renders visual phase groups in pipeline order including the decision phase', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const mkStep = (
      id: string,
      displayName: string,
      kind: NonNullable<TaskPipelineResponse['pipeline']['allSteps']>[number]['kind'],
    ) => ({ id, displayName, kind, runMode: 'sequential' as const, dependsOn: [], idempotent: true, stub: false });
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          mkStep('pre-loop-guard', 'Loop check', 'module'),
          mkStep('core-agent-run', 'Agent execution', 'core'),
          mkStep('aspect-code-quality', 'Code quality', 'aspect'),
          mkStep('post-lint-scss', 'Frontend stylelint', 'tool'),
          mkStep('post-orchestrator-decision', 'Final verdict', 'orchestrator'),
          mkStep('post-drift-adr-code', 'Drift: ADR / Code', 'drift'),
        ],
      },
      execution: null,
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const groups = Array.from(
      fixture.nativeElement.querySelectorAll('[data-testid="overview-pipeline-phase"]'),
    ) as HTMLElement[];
    expect(groups.map(g => g.querySelector('.ov-pl-phase__label')?.textContent?.trim())).toEqual([
      'PRE STEPS', 'CORE AGENT WORK', 'ASPECT', 'TOOL', 'DECISION', 'DRIFT',
    ]);
    expect(groups.map(g => g.querySelector('.ov-pl-phase__info')?.textContent?.trim())).toEqual([
      'i', 'i', 'i', 'i', 'i', 'i',
    ]);
    expect(groups.some(g => g.querySelector('.ov-pl-phase__description'))).toBe(false);
    // Each header is a real toggle button carrying an accessible name that folds
    // in the aggregate state (state itself rides on aria-expanded, not colour).
    expect(groups.every(g => g.tagName === 'BUTTON')).toBe(true);
    expect(groups.map(g => g.getAttribute('aria-label'))).toEqual([
      'PRE STEPS phase, pending, 1 step. Preparation checks before the agent gets the task.',
      'CORE AGENT WORK phase, pending, 1 step. The coding agent work.',
      'ASPECT phase, pending, 1 step. Parallel review passes over the finished work.',
      'TOOL phase, pending, 1 step. Deterministic post-run tooling and evidence steps.',
      'DECISION phase, pending, 1 step. The orchestrator ruling that accepts, reissues, or escalates.',
      'DRIFT phase, pending, 1 step. Optional drift-analysis passes.',
    ]);
    expect(groups.map(g => g.getAttribute('data-phase'))).toEqual([
      'pre', 'core', 'aspect', 'tool', 'decision', 'drift',
    ]);

    // Nothing has run (execution === null) -> the Empty scenario collapses every
    // section, so no step rows render and every marker shows the "+" affordance.
    expect(groups.map(g => g.getAttribute('aria-expanded'))).toEqual(
      ['false', 'false', 'false', 'false', 'false', 'false'],
    );
    expect(groups.map(g => g.querySelector('.ov-pl-phase__marker')?.textContent?.trim())).toEqual(
      ['+', '+', '+', '+', '+', '+'],
    );
    expect(fixture.nativeElement.querySelectorAll('[data-testid="overview-pipeline-step"]').length).toBe(0);

    // Expanding reveals the per-step rows in pipeline order, one row per phase.
    fixture.componentInstance.expandAllPipelineGroups();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    const stepPhases = Array.from(
      fixture.nativeElement.querySelectorAll('[data-testid="overview-pipeline-step"]'),
    ).map(el => (el as HTMLElement).getAttribute('data-phase'));
    expect(stepPhases).toEqual(['pre', 'core', 'aspect', 'tool', 'decision', 'drift']);
  });

  it('pipeline block: sections carry aggregate tone and default collapse for a mid-flight run', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'pre-loop-guard', displayName: 'Loop check', kind: 'module', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
          { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
          { id: 'post-orchestrator-decision', displayName: 'Final verdict', kind: 'orchestrator', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: null,
        steps: [
          { stepId: 'pre-loop-guard', kind: 'module', status: 'passed', startedAt: '2026-06-02T08:00:00Z', completedAt: '2026-06-02T08:00:01Z', durationMs: 900, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
          { stepId: 'core-agent-run', kind: 'core', model: 'claude-opus-4-7', status: 'running', startedAt: new Date().toISOString(), completedAt: null, durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
        ],
      },
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const byPhase = Object.fromEntries(c.pipelineGroups().map(g => [g.phaseKey, g]));

    // Tone: the finished PRE section reads ok, the live CORE section reads warn,
    // the not-yet-reached ASPECT / DECISION sections read neutral.
    expect(byPhase['pre'].tone).toBe('ok');
    expect(byPhase['core'].tone).toBe('warn');
    expect(byPhase['aspect'].tone).toBe('neutral');

    // Default collapse mid-flight: the running section (warn + frontier) opens,
    // the quiet finished PRE section recedes, and the not-yet-reached tail sections
    // stay collapsed.
    expect(c.isGroupCollapsed(byPhase['core'])).toBe(false);
    expect(c.isGroupCollapsed(byPhase['pre'])).toBe(true);
    expect(c.isGroupCollapsed(byPhase['aspect'])).toBe(true);

    // The header carries the tone + status word so state never rides on colour alone.
    const host = fixture.nativeElement as HTMLElement;
    const coreHeader = host.querySelector('[data-testid="overview-pipeline-group"][data-phase="core"] [data-testid="overview-pipeline-phase"]');
    expect(coreHeader?.getAttribute('data-tone')).toBe('warn');
    expect(coreHeader?.getAttribute('aria-expanded')).toBe('true');
    expect(coreHeader?.querySelector('[data-testid="overview-pipeline-phase-summary"] .ov-pl-phase__status')?.textContent?.trim())
      .toBe('Running');

    // Toggling is an explicit operator override in both directions.
    c.toggleGroup(byPhase['pre']);
    expect(c.isGroupCollapsed(byPhase['pre'])).toBe(false);
    c.toggleGroup(byPhase['core']);
    expect(c.isGroupCollapsed(byPhase['core'])).toBe(true);
  });

  it('pipeline block: every step row carries a "what happens here" explanation tooltip keyed by step id', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    type Step = NonNullable<TaskPipelineResponse['pipeline']['allSteps']>[number];
    const mkStep = (id: string, displayName: string, kind: Step['kind']): Step =>
      ({ id, displayName, kind, runMode: 'sequential', dependsOn: [], idempotent: true, stub: false });
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          mkStep('pre-loop-guard', 'Loop check', 'module'),
          mkStep('pre-orchestrator-prep', 'Orchestrator prep', 'module'),
          mkStep('pre-reissue-open-items', 'Reissue open-items check', 'module'),
          mkStep('core-agent-run', 'Agent execution', 'core'),
          mkStep('aspect-requirement-fit', 'Requirement fit', 'aspect'),
          mkStep('aspect-code-quality', 'Code quality', 'aspect'),
          mkStep('aspect-documentation-impact', 'Documentation impact', 'aspect'),
          mkStep('aspect-tests-and-evidence', 'Tests and evidence', 'aspect'),
          mkStep('post-git-commit-attribution', 'Git commit attribution', 'tool'),
          mkStep('post-lint-scss', 'Frontend stylelint', 'tool'),
          mkStep('post-regression-radar', 'Regression radar', 'tool'),
          mkStep('post-orchestrator-decision', 'Auto-review decision', 'orchestrator'),
          mkStep('post-drift-adr-code', 'Drift: ADR / Code', 'drift'),
        ],
      },
      execution: null,
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const rows = fixture.componentInstance.pipelineRows();

    // Every row has an explanation whose title is the step's display label and
    // whose body is non-empty (no bare rows).
    for (const row of rows) {
      expect(row.explanation).toBeTruthy();
      expect(row.explanation.title).toBe(row.label);
      expect(row.explanation.body.length).toBeGreaterThan(0);
    }

    const byId = (id: string) => rows.find(r => r.id === id)!;
    expect(byId('pre-loop-guard').explanation.body).toContain('loop guard');
    expect(byId('core-agent-run').explanation.body).toContain('coding seat');
    expect(byId('aspect-requirement-fit').explanation.body).toContain('acceptance criteria');
    expect(byId('post-git-commit-attribution').explanation.body).toContain('git commits');
    expect(byId('post-regression-radar').explanation.body).toContain('spec-change');
    expect(byId('post-orchestrator-decision').explanation.body).toContain('final ruling');
    expect(byId('post-drift-adr-code').explanation.body).toContain('off by default');

    // The DOM renders the tooltip-bearing name span once per step (sections start
    // collapsed with no run, so expand them to bring every row into the DOM).
    fixture.componentInstance.expandAllPipelineGroups();
    try { fixture.detectChanges(); } catch { /* ignore */ }
    const names = fixture.nativeElement.querySelectorAll('[data-testid="overview-pipeline-step-name"]');
    expect(names.length).toBe(rows.length);
  });

  it('pipeline block: an unknown step id falls back to a per-kind explanation rather than rendering bare', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'post-future-experimental', displayName: 'Future step', kind: 'tool', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: null,
      cost: emptyCost(),
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const row = fixture.componentInstance.pipelineRows()[0];
    expect(row.explanation.title).toBe('Future step');
    // Falls back to the StepKind ('tool') copy.
    expect(row.explanation.body).toContain('tooling step');
  });

  it('promote affordance: shown only on a finished planning task across its finished lanes', async () => {
    const fixture = await build(baseJob({ mode: 'planning', state: '4-auto-review' }));
    const c = fixture.componentInstance;
    expect(c.canPromote()).toBe(true);
    for (const state of ['5-human-review', '6-completed']) {
      fixture.componentRef.setInput('job', baseJob({ mode: 'planning', state }));
      try { fixture.detectChanges(); } catch { /* ignore */ }
      expect(c.canPromote()).toBe(true);
    }
  });

  it('promote affordance: hidden on a planning task that has not finished', async () => {
    const fixture = await build(baseJob({ mode: 'planning', state: '1-preparation' }));
    const c = fixture.componentInstance;
    expect(c.canPromote()).toBe(false);
    for (const state of ['2-ready', '3-progress', '1b-needs-human-review']) {
      fixture.componentRef.setInput('job', baseJob({ mode: 'planning', state }));
      try { fixture.detectChanges(); } catch { /* ignore */ }
      expect(c.canPromote()).toBe(false);
    }
  });

  it('promote affordance: hidden on research tasks even when finished (research is read-only)', async () => {
    const fixture = await build(baseJob({ mode: 'research', state: '6-completed' }));
    expect(fixture.componentInstance.canPromote()).toBe(false);
  });

  it('promote affordance: hidden on coding tasks and on legacy payloads with no mode', async () => {
    const fixture = await build(baseJob({ mode: 'coding', state: '6-completed' }));
    const c = fixture.componentInstance;
    expect(c.canPromote()).toBe(false);

    // Legacy payloads omit `mode`; the affordance must stay hidden (read as coding).
    fixture.componentRef.setInput('job', baseJob({ state: '6-completed' }));
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(c.canPromote()).toBe(false);
  });

  it('promote affordance: the promote button is in the DOM for a finished planning task, absent for research', async () => {
    const fixture = await build(baseJob({ mode: 'planning', state: '5-human-review' }));
    expect(
      fixture.nativeElement.querySelector('[data-testid="overview-promote-btn"]'),
    ).not.toBeNull();

    fixture.componentRef.setInput('job', baseJob({ mode: 'research', state: '5-human-review' }));
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(
      fixture.nativeElement.querySelector('[data-testid="overview-promote-btn"]'),
    ).toBeNull();
  });

  it('promote affordance: the compact spawn-panel action delegates to the existing promote flow', async () => {
    const fixture = await build(baseJob({
      mode: 'planning',
      state: '5-human-review',
      planningSpawn: {
        spawned: [],
        spawnedCount: 0,
        noFollowUpDeclared: false,
        contractSatisfied: false,
      },
    }));
    const promote = vi.spyOn(fixture.componentInstance, 'promote').mockImplementation(() => undefined);

    (fixture.nativeElement.querySelector('[data-testid="overview-promote-btn"]') as HTMLButtonElement).click();

    expect(promote).toHaveBeenCalledOnce();
  });

  it('agent-execution row: run count is read from the run-timeline (same source as the Overview Runs value)', async () => {
    const lastEndedAt = '2026-06-02T09:12:00Z';
    const runs = [
      runRecord({ index: 0, intent: 'start', startedAt: '2026-06-02T08:00:00Z', endedAt: '2026-06-02T08:12:00Z' }),
      runRecord({ index: 1, intent: 'continue', startedAt: '2026-06-02T08:30:00Z', endedAt: '2026-06-02T08:45:00Z' }),
      runRecord({ index: 2, intent: 'recovery', startedAt: '2026-06-02T09:00:00Z', endedAt: lastEndedAt }),
    ];
    const fixture = await build(
      baseJob({ state: '3-progress', sessionName: 'sess-xyz', cliType: 'claude', model: 'claude-opus-4-8' }),
    );
    TestBed.inject(TaskPipelinePollService).pipeline.set(agentPipeline());
    TestBed.inject(RunTimelinePollService).timeline.set(runTimeline(34, runs, { lastActivityAt: null }));
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    expect(c.agentRunCount()).toBe(34);
    expect(c.agentRunCountLabel()).toBe('34 runs');

    const tip = c.agentRunTooltip()!;
    expect(tip).not.toBeNull();
    expect(tip.title).toBe('Agent execution · 34 runs');
    expect(tip.body).toContain('Runs: 34');
    expect(tip.body).toContain('Recovered: 1');
    expect(tip.body).toContain('Model: claude-opus-4-8');
    expect(tip.body).toContain('Session: sess-xyz');
    expect(tip.body).toContain(`Last activity: ${c.formatAbsoluteTime(lastEndedAt)}`);
    expect(tip.body).toContain('See the Timeline tab');

    // The badge renders only on the core row, as a focusable <button>.
    const badge = fixture.nativeElement.querySelector('[data-testid="overview-pipeline-agent-runs"]') as HTMLElement | null;
    expect(badge).not.toBeNull();
    expect(badge!.tagName).toBe('BUTTON');
    expect(badge!.textContent?.trim()).toBe('34 runs');
    expect(badge!.getAttribute('data-run-count')).toBe('34');
    expect(badge!.getAttribute('aria-label')).toContain('34 runs');
  });

  it('agent-execution row: falls back to the Agent Work call count when the run-timeline is empty', async () => {
    const fixture = await build(
      baseJob({ state: '3-progress' }),
      {
        calls: 5, recovered: true, toolCalls: 0, toolCounts: [],
        startedAt: new Date().toISOString(), lastTouchAt: new Date().toISOString(),
        currentSessionId: 'sess-aw',
      },
    );
    TestBed.inject(TaskPipelinePollService).pipeline.set(agentPipeline());
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    expect(c.agentRunCount()).toBe(5);
    expect(c.agentRunCountLabel()).toBe('5 runs');
    // Recovered flag (no run-timeline intents available) still surfaces as 1.
    expect(c.agentRunTooltip()!.body).toContain('Recovered: 1');
    expect(c.agentRunTooltip()!.body).toContain('Session: sess-aw');
  });

  it('agent-execution row: a single run reads as "1 run"', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    TestBed.inject(TaskPipelinePollService).pipeline.set(agentPipeline());
    TestBed.inject(RunTimelinePollService).timeline.set(runTimeline(1, [runRecord()]));
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    expect(c.agentRunCount()).toBe(1);
    expect(c.agentRunCountLabel()).toBe('1 run');
    expect(c.agentRunTooltip()!.title).toBe('Agent execution · 1 run');
  });

  it('agent-execution row: no runs keeps the dash state — no badge, no tooltip', async () => {
    const fixture = await build(baseJob({ state: '2-ready' }));
    TestBed.inject(TaskPipelinePollService).pipeline.set(agentPipeline());
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    expect(c.agentRunCount()).toBe(0);
    expect(c.agentRunTooltip()).toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-testid="overview-pipeline-agent-runs"]'),
    ).toBeNull();
  });

  it('agent-execution row: the badge is bound to the core row only (aspect rows never carry it)', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    TestBed.inject(TaskPipelinePollService).pipeline.set(agentPipeline());
    TestBed.inject(RunTimelinePollService).timeline.set(runTimeline(7, [runRecord()]));
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const badges = fixture.nativeElement.querySelectorAll('[data-testid="overview-pipeline-agent-runs"]');
    // Exactly one core row exists, so exactly one badge — never on the aspect row.
    expect(badges.length).toBe(1);
    const coreRow = badges[0].closest('[data-testid="overview-pipeline-step"]');
    expect(coreRow?.getAttribute('data-step-id')).toBe('core-agent-run');
  });

  it('total duration: sums the per-run durations when the run-timeline carries them', async () => {
    const fixture = await build(baseJob({ state: '6-completed' }));
    TestBed.inject(RunTimelinePollService).timeline.set(
      runTimeline(2, [runRecord({ durationSeconds: 10 }), runRecord({ durationSeconds: 5 })]),
    );
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(fixture.componentInstance.totalDuration()).toBe(15);
  });

  it('total duration: falls back to the persisted CORE step duration when a killed run left no run-row duration (ASS-665)', async () => {
    // ASS-665: a run was killed mid-flight. Its exit marker never paired with a
    // session-event, so the run-timeline carries no duration - but the CORE
    // pipeline step persisted the real elapsed time (1215.9s). The Overview
    // "Total Duration" stat must still surface it even though tokens / run rows
    // are missing.
    const fixture = await build(baseJob({ state: '5-human-review' }));
    const pipe = agentPipeline();
    pipe.execution!.steps[0] = {
      ...pipe.execution!.steps[0],
      status: 'failed',
      durationMs: 1_215_900,
      completedAt: new Date().toISOString(),
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    // No run-timeline set: runs() is empty, so the sum is 0 and the fallback runs.
    try { fixture.detectChanges(); } catch { /* ignore */ }
    const c = fixture.componentInstance;
    expect(c.totalDuration()).toBeCloseTo(1215.9, 1);
  });

  it('total duration: run-row durations win over the CORE-step fallback when both exist', async () => {
    const fixture = await build(baseJob({ state: '6-completed' }));
    const pipe = agentPipeline();
    pipe.execution!.steps[0] = { ...pipe.execution!.steps[0], status: 'passed', durationMs: 999_000 };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    TestBed.inject(RunTimelinePollService).timeline.set(
      runTimeline(1, [runRecord({ durationSeconds: 7 })]),
    );
    try { fixture.detectChanges(); } catch { /* ignore */ }
    // The real run duration (7s) is authoritative; the fallback is not used.
    expect(fixture.componentInstance.totalDuration()).toBe(7);
  });

  it('session row was removed (component no longer exposes session-id helpers)', async () => {
    const fixture = await build(baseJob({ sessionName: 'c705779a-aaaa-bbbb-cccc-ddddeeeeffff' }));
    // The overview no longer surfaces session id in any row. The session
    // chain remains visible on the protocol pane's session badge. The
    // shortSessionId / copyToClipboard helpers were dropped from the
    // controller; assert they are gone so a future re-add lights up here.
    const proto = OverviewPaneComponent.prototype as unknown as Record<string, unknown>;
    expect(typeof proto['shortSessionId']).toBe('undefined');
    expect(typeof proto['copyToClipboard']).toBe('undefined');
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('shows loop-waiting with elapsed time in task detail', async () => {
    const fixture = await build(baseJob({
      state: '3-progress',
      phase: 'loop-waiting',
      phaseEnteredAt: new Date(Date.now() - 42_000).toISOString(),
    }));
    fixture.detectChanges();
    await fixture.whenStable();

    const phase = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="overview-title-phase"]');
    expect(phase?.textContent).toContain('Waiting for loop continuation 0:42');
  });

  it('keeps overview content on reusable left-aligned measures', async () => {
    const fixture = await build(baseJob());
    TestBed.inject(TaskPipelinePollService).pipeline.set(agentPipeline());
    fixture.detectChanges();
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    const title = host.querySelector('[data-testid="overview-title-block"]');
    const status = host.querySelector('[data-testid="overview-status"]');
    const agent = host.querySelector('[data-testid="overview-agent"]');
    const pipeline = host.querySelector('[data-testid="overview-pipeline"]');
    const consumption = host.querySelector('[data-testid="overview-consumption"]');

    expect(title?.classList.contains('studio-measure')).toBe(true);
    expect(title?.classList.contains('studio-measure--prose')).toBe(true);
    expect(status?.classList.contains('studio-measure--tabular')).toBe(true);
    expect(agent?.closest('[data-testid="overview-status"]')).toBe(status);
    expect(agent?.classList.contains('studio-measure--tabular')).toBe(false);
    expect(pipeline?.closest('[data-testid="overview-consumption"]')).toBe(consumption);
    expect(pipeline?.classList.contains('studio-measure--tabular-compact')).toBe(false);
    expect(consumption?.classList.contains('studio-measure--tabular-compact')).toBe(true);
  });
});
