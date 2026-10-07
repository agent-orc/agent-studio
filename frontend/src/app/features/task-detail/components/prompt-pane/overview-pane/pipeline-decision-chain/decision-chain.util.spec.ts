import { describe, expect, it } from 'vitest';
import type {
  PipelineExecutionRecord,
  PipelineStepExecution,
} from '../../../../../task-pipeline/models/task-pipeline.model';
import { summarizeDecisionCost } from '../../../../../task-pipeline/decision-cost.util';
import { buildDecisionTransitions, decisionCost } from './decision-chain.util';

function step(partial: Partial<PipelineStepExecution> & Pick<PipelineStepExecution, 'stepId' | 'kind'>): PipelineStepExecution {
  return {
    status: 'passed',
    durationMs: 0,
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    ...partial,
  };
}

function record(steps: PipelineStepExecution[]): PipelineExecutionRecord {
  return {
    pipelineId: 'standard-task-pipeline',
    pipelineVersion: 1,
    jobId: 'J1',
    project: 'P',
    startedAt: '2026-10-04T10:00:00Z',
    steps,
  };
}

const label = (id: string) => ({
  'core-agent-run': 'Agent execution',
  'aspect-code-quality': 'Code quality',
  'post-orchestrator-decision': 'Auto-review decision',
  'post-orchestrator-review': 'Post-core review',
}[id] ?? id);

describe('buildDecisionTransitions', () => {
  it('orders every decision execution in time, earlier rounds included, with the step that ended before it', () => {
    const transitions = buildDecisionTransitions(record([
      step({ stepId: 'core-agent-run', kind: 'core', startedAt: '2026-10-04T10:00:00Z', completedAt: '2026-10-04T10:10:00Z' }),
      step({
        stepId: 'post-orchestrator-review', kind: 'orchestrator',
        startedAt: '2026-10-04T10:10:01Z', completedAt: '2026-10-04T10:10:01Z',
        verdict: 'pass', costBasis: 'deterministic', estimatedCostUsd: 0,
      }),
      step({
        stepId: 'aspect-code-quality', kind: 'aspect', model: 'claude-haiku-4-5',
        startedAt: '2026-10-04T10:10:05Z', completedAt: '2026-10-04T10:11:00Z',
      }),
      step({
        stepId: 'post-orchestrator-decision', kind: 'orchestrator',
        startedAt: '2026-10-04T10:20:00Z', completedAt: '2026-10-04T10:20:02Z', durationMs: 2000,
        verdict: 'accept', model: 'gpt-5.4-mini', modelSource: 'config', costBasis: 'model',
        modelPriced: true, estimatedCostUsd: 0.0123, inputTokens: 900, outputTokens: 100,
        evidenceRef: 'decision.md', runs: 2,
        earlierRuns: [{
          status: 'passed', startedAt: '2026-10-04T10:11:01Z', completedAt: '2026-10-04T10:11:03Z',
          durationMs: 2000, model: 'gpt-5.4-mini', inputTokens: 800, outputTokens: 50,
          cacheReadTokens: 0, cacheCreationTokens: 0, costBasis: 'model', modelPriced: true,
          estimatedCostUsd: 0.01, verdict: 'reissue',
        }],
      }),
    ]), label);

    expect(transitions.map(t => [t.fromLabel, t.decisionLabel, t.verdict, t.occurrence])).toEqual([
      ['Agent execution', 'Post-core review', 'pass', 1],
      ['Code quality', 'Auto-review decision', 'reissue', 1],
      ['Code quality', 'Auto-review decision', 'accept', 2],
    ]);
    const last = transitions[2];
    expect(last.model).toBe('gpt-5.4-mini');
    expect(last.decidedBy).toBe('model');
    expect(last.modelSource).toBe('config');
    expect(last.costText).toBe('$0.01');
    expect(last.evidenceRef).toBe('decision.md');
    expect(last.occurrences).toBe(2);
    expect(transitions[0].decidedBy).toBe('rule');
    expect(transitions[0].costText).toBe('$0 · rule');
    expect(new Set(transitions.map(t => t.key)).size).toBe(3);
  });

  it('returns nothing before a run', () => {
    expect(buildDecisionTransitions(null, label)).toEqual([]);
  });
});

describe('decisionCost', () => {
  const base = { inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheCreationTokens: 0, model: 'm' };

  it('never renders an unpriced model as zero', () => {
    expect(decisionCost({ ...base, costBasis: 'model', modelPriced: false, estimatedCostUsd: null }))
      .toEqual({ text: 'no price data', tone: 'unpriced' });
  });

  it('marks a model-backed row without a model as not measured', () => {
    expect(decisionCost({ ...base, costBasis: null, modelPriced: null, estimatedCostUsd: null }).tone).toBe('unmeasured');
  });

  it('renders a deterministic row as a measured zero', () => {
    expect(decisionCost({ ...base, costBasis: 'deterministic', estimatedCostUsd: 0 }).tone).toBe('zero');
  });
});

describe('summarizeDecisionCost', () => {
  it('states the unpriced share beside the priced comparison', () => {
    const summary = summarizeDecisionCost({
      deciding: { tokens: 1_006_000, pricedCostUsd: 2, unpricedTokens: 6_000, runs: 3, unpricedRuns: 1 },
      agentRuns: { tokens: 5_000_000, pricedCostUsd: 38, unpricedTokens: 0, runs: 2, unpricedRuns: 0 },
      other: { tokens: 0, pricedCostUsd: 0, unpricedTokens: 0, runs: 4, unpricedRuns: 0 },
    })!;
    expect(summary.decidingText).toBe('$2.00+');
    expect(summary.agentRunsText).toBe('$38.00');
    expect(summary.otherText).toBe('$0');
    expect(summary.decidingShare).toBe('5.0%');
    expect(summary.unpricedText).toBe('6,000 tokens on 1 run without a price');
  });

  it('reads an all-unpriced bucket as no price data, not $0', () => {
    const summary = summarizeDecisionCost({
      deciding: { tokens: 500, pricedCostUsd: 0, unpricedTokens: 500, runs: 1, unpricedRuns: 1 },
      agentRuns: { tokens: 0, pricedCostUsd: 0, unpricedTokens: 0, runs: 0, unpricedRuns: 0 },
      other: { tokens: 0, pricedCostUsd: 0, unpricedTokens: 0, runs: 0, unpricedRuns: 0 },
    })!;
    expect(summary.decidingText).toBe('no price data');
    expect(summary.decidingShare).toBeNull();
  });
});
