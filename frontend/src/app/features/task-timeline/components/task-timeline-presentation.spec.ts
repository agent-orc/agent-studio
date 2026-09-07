import { describe, expect, it } from 'vitest';
import { TIMELINE_KIND, type TaskTimelineEvent } from '../models/task-timeline.model';
import {
  executionContextDisclosure,
  timelineDetailEntries,
  timelineEventReason,
  timelineEventSummary,
  timelineEventTitle,
  timelineKindLabel,
} from './task-timeline-presentation';

function event(overrides: Partial<TaskTimelineEvent> = {}): TaskTimelineEvent {
  return {
    ts: '2026-07-28T10:00:00Z',
    kind: TIMELINE_KIND.promptCreated,
    actor: 'system',
    summary: '',
    ...overrides,
  };
}

describe('task timeline presentation', () => {
  it('provides a quiet human label for every closed event kind', () => {
    for (const kind of Object.values(TIMELINE_KIND)) {
      const label = timelineKindLabel(kind);
      expect(label).not.toBe(kind);
      expect(label).not.toContain('_');
    }
  });

  it('folds a generated run-finish summary and status into one title', () => {
    const completed = event({
      kind: TIMELINE_KIND.agentRunFinished,
      summary: 'codex run completed after 247.6s',
      details: { cli: 'codex', status: 'completed' },
    });

    expect(timelineEventTitle(completed)).toBe('Run finished · 247.6s');
    expect(timelineEventSummary(completed)).toBeNull();
    expect(timelineDetailEntries(completed)).toEqual([]);
  });

  it('presents automatic model migration as a calm audit event', () => {
    const migrated = event({
      kind: TIMELINE_KIND.modelMigrated,
      summary: 'Model migrated from claude-sonnet-4-6 to claude-sonnet-5.',
      details: {
        from: 'claude-sonnet-4-6',
        to: 'claude-sonnet-5',
        rule: 'latestInFamily:claude-sonnet',
        catalogVersion: '2026-09-06',
      },
    });

    expect(timelineEventTitle(migrated))
      .toBe('Model updated automatically · claude-sonnet-4-6 to claude-sonnet-5');
    expect(timelineEventSummary(migrated)).toBeNull();
    expect(timelineDetailEntries(migrated)).toEqual([
      { key: 'rule', label: 'Rule', value: 'latestInFamily:claude-sonnet' },
      { key: 'catalogVersion', label: 'Catalog Version', value: '2026-09-06' },
    ]);
  });

  it('removes generated lifecycle wording already carried by the title', () => {
    const started = event({
      kind: TIMELINE_KIND.agentRunStarted,
      summary: 'codex CLI start',
      details: { cli: 'codex', model: 'gpt-5.6-sol' },
    });
    const slot = event({
      kind: TIMELINE_KIND.runnerSlotAdmission,
      summary: 'Admitted to slot 1/2: predicted scope is disjoint',
      details: { slot: '1', maxParallelism: '2', decision: 'parallel-ok' },
    });
    const recovery = event({
      kind: TIMELINE_KIND.integrationRecoveryQueued,
      summary: 'Integration recovery queued: rebase task/AGT-2412 onto develop.',
    });

    expect(timelineEventTitle(started)).toBe('Run started');
    expect(timelineEventSummary(started)).toBeNull();
    expect(timelineEventTitle(slot)).toBe('Slot admitted · 1/2');
    expect(timelineEventSummary(slot)).toBe('Predicted scope is disjoint');
    expect(timelineDetailEntries(slot)).toEqual([
      { key: 'decision', label: 'Decision', value: 'parallel-ok' },
    ]);
    expect(timelineEventSummary(recovery)).toBe('Rebase task/AGT-2412 onto develop.');
  });

  it('folds generated review, step, and decomposition summaries into their titles', () => {
    const preStep = event({
      kind: TIMELINE_KIND.preStepFinished,
      summary: 'Context retrieval completed',
      details: { step: 'context-retrieval', status: 'completed', durationMs: '1280' },
    });
    const review = event({
      kind: TIMELINE_KIND.humanReviewDecided,
      summary: 'Human review accepted the delivery',
      details: { decision: 'accept', reviewer: 'robert@example.com' },
    });
    const epic = event({
      kind: TIMELINE_KIND.epicDecomposed,
      summary: 'Epic decomposed into 3 tasks',
      details: { created: '3', targetState: '1-backlog' },
    });

    expect(timelineEventTitle(preStep)).toBe('Pre-step finished · Context retrieval');
    expect(timelineEventSummary(preStep)).toBeNull();
    expect(timelineEventTitle(review)).toBe('Human review accepted');
    expect(timelineEventSummary(review)).toBeNull();
    expect(timelineEventTitle(epic)).toBe('Epic decomposed · 3 tasks');
    expect(timelineEventSummary(epic)).toBeNull();
  });

  it.each([
    ['NotApplicable', '', 'not applicable'],
    ['Skipped', 'no verify commands derivable', 'not applicable'],
    ['Skipped', 'pipeline interrupted before command execution', 'skipped'],
  ])('keeps pipeline terminal status %s explicit', (status, reason, label) => {
    const gate = event({
      kind: TIMELINE_KIND.postStepFinished,
      summary: `Build/test gate ${label}`,
      details: { step: 'post-build-test-gate', status, reason },
    });

    expect(timelineEventTitle(gate)).toBe(`Post-step ${label} · Build/test gate`);
    expect(timelineEventSummary(gate)).toBeNull();
  });

  it('keeps a non-generated run-finish note that adds information', () => {
    const completed = event({
      kind: TIMELINE_KIND.agentRunFinished,
      summary: 'Agent reported that visual evidence could not be captured',
      details: { status: 'completed', durationSeconds: '15.2' },
    });

    expect(timelineEventTitle(completed)).toBe('Run finished · 15.2s');
    expect(timelineEventSummary(completed)).toContain('visual evidence');
  });

  it('removes defaults, zero counts, CLI duplication, and values already told in prose', () => {
    const started = event({
      kind: TIMELINE_KIND.agentRunStarted,
      summary: 'codex CLI start',
      details: {
        cli: 'codex',
        model: 'gpt-5.6-sol',
        quotaFallback: 'false',
        resumed: 'false',
        mcp: '0',
        fallbackReason: '',
        intent: 'start',
      },
    });

    expect(timelineDetailEntries(started)).toEqual([
      { key: 'model', label: 'Model', value: 'gpt-5.6-sol' },
    ]);
  });

  it('omits route CLI fields when their paired models already identify the clients', () => {
    const fallback = event({
      kind: TIMELINE_KIND.quotaFallbackActivated,
      summary: 'Quota fallback activated',
      details: {
        primaryCli: 'codex',
        primaryModel: 'gpt-5.6-sol',
        fallbackCli: 'codex',
        fallbackModel: 'gpt-5.6-terra',
      },
    });

    expect(timelineDetailEntries(fallback)).toEqual([
      { key: 'primaryModel', label: 'Primary Model', value: 'gpt-5.6-sol' },
      { key: 'fallbackModel', label: 'Fallback Model', value: 'gpt-5.6-terra' },
    ]);
  });

  it('does not repeat a reason already present in the event story', () => {
    const requeued = event({
      kind: TIMELINE_KIND.operatorRequeued,
      summary: 'Operator reopened the task for fresh assessment: verify density',
      details: { reason: 'verify density' },
    });

    expect(timelineEventReason(requeued)).toBeNull();
  });

  it('turns the execution-context count into a disclosure backed by exact sources', () => {
    const context = event({
      kind: TIMELINE_KIND.executionContext,
      summary: 'codex context: 3 sources, model gpt-5.6-sol, YOLO',
      details: {
        cli: 'codex',
        source: 'convention',
        sources: '2',
        mcp: '0',
        model: 'gpt-5.6-sol',
        thinkingLevel: 'medium',
        sourceItems: JSON.stringify([
          { kind: 'memory', label: 'AGENTS.md', path: '/repo/AGENTS.md', exists: true, detail: null },
          { kind: 'global-config', label: 'Codex config', path: '/home/.codex/config.toml', exists: true, detail: null },
        ]),
      },
    });

    expect(timelineEventTitle(context)).toBe('Execution context');
    expect(timelineEventSummary(context)).toBeNull();
    expect(timelineDetailEntries(context)).toEqual([]);
    expect(executionContextDisclosure(context)).toMatchObject({
      label: '2 sources',
      origin: 'Codex config conventions',
      sources: [{ label: 'AGENTS.md' }, { label: 'Codex config' }],
    });
  });

  it('omits an unexpandable legacy source count when no source elements survive', () => {
    const context = event({
      kind: TIMELINE_KIND.executionContext,
      details: { cli: 'codex', source: 'convention', sources: '3', mcp: '0' },
    });

    expect(executionContextDisclosure(context)).toBeNull();
  });
});
