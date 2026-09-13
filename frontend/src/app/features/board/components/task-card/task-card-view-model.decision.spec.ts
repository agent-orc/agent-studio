import { describe, expect, it } from 'vitest';
import { buildDecisionBadge } from './task-card-view-model';
import type { DecisionContent, TaskInfo } from '../../../../models/task.model';

/**
 * AGT-2795: the decision badge + decider surface a decision card's type and
 * owner at a glance on the board. An open decision reads as "Decision" and a
 * settled one as "Decided".
 */
function decision(overrides: Partial<DecisionContent> = {}): DecisionContent {
  return {
    question: 'Lock file or not?',
    options: [
      { id: 'a', label: 'Lock file' },
      { id: 'b', label: 'No lock file' },
    ],
    decider: 'operator',
    status: 'requested',
    ...overrides,
  };
}

function job(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return { kind: 'decision', decision: decision(), ...overrides } as unknown as TaskInfo;
}

describe('buildDecisionBadge', () => {
  it('returns null for a non-decision card', () => {
    expect(buildDecisionBadge({ kind: 'task' } as TaskInfo)).toBeNull();
  });

  it('returns null for a decision kind with no decision content', () => {
    expect(buildDecisionBadge({ kind: 'decision' } as TaskInfo)).toBeNull();
  });

  it('labels an open decision "Decision" and carries the decider', () => {
    const badge = buildDecisionBadge(job());
    expect(badge).not.toBeNull();
    expect(badge!.label).toBe('Decision');
    expect(badge!.status).toBe('requested');
    expect(badge!.decider).toBe('operator');
    expect(badge!.tooltip).toContain('operator');
  });

  it('labels a settled decision "Decided"', () => {
    const badge = buildDecisionBadge(
      job({ decision: decision({ status: 'decided', decidedBy: 'alice' }) }),
    );
    expect(badge!.label).toBe('Decided');
    expect(badge!.status).toBe('decided');
    expect(badge!.tooltip).toContain('alice');
  });
});
