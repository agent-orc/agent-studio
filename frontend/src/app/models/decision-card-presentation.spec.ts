import { describe, expect, it } from 'vitest';
import {
  buildDecisionBadge,
  buildDecisionInboxItems,
  decisionBlockReason,
  decisionBlockers,
  decisionMoveRefusal,
  deciderName,
  isDecisionOpen,
  isDecisionOverdue,
  openDecisionCards,
  recommendedOption,
} from './decision-card-presentation';
import type { DecisionContent, TaskInfo } from './task.model';

function decision(overrides: Partial<DecisionContent> = {}): DecisionContent {
  return {
    question: 'Lock file or not?',
    options: [{ id: 'a', label: 'Lock file' }, { id: 'b', label: 'No lock file' }],
    decider: 'operator',
    status: 'pending',
    dependants: ['AGT-2793'],
    ...overrides,
  };
}

function job(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'j', taskKey: 'p::j', key: 'AGT-1', title: 'T', state: '1-preparation',
    projectName: 'P', createdAt: '2026-09-13T09:00:00Z', ...overrides,
  } as TaskInfo;
}

describe('decision-card-presentation (AGT-2795)', () => {
  it('reads only "decided" as settled, like backend DecisionStatuses.Normalize', () => {
    expect(isDecisionOpen({ status: 'pending' })).toBe(true);
    expect(isDecisionOpen({ status: 'requested' })).toBe(true);
    expect(isDecisionOpen({ status: 'decided' })).toBe(false);
    expect(isDecisionOpen(null)).toBe(true);
  });

  it('names the decider: operator default, roles, and registered identities', () => {
    expect(deciderName('operator')).toBe('Operator');
    expect(deciderName('')).toBe('Operator');
    expect(deciderName('role:reviewer')).toBe('Reviewer');
    expect(deciderName('owner')).toBe('Owner');
    expect(deciderName('client-7', (id) => (id === 'client-7' ? 'Robin' : null))).toBe('Robin');
    expect(deciderName('client-8', () => null)).toBe('client-8');
  });

  it('builds the kind badge only for decision cards', () => {
    expect(buildDecisionBadge(job({ kind: 'task' }))).toBeNull();
    expect(buildDecisionBadge(job({ kind: 'decision' }))).toBeNull();
    const open = buildDecisionBadge(job({ kind: 'decision', decision: decision() }))!;
    expect(open).toMatchObject({ open: true, label: 'Decision', decider: 'Operator' });
    expect(open.tooltip).toContain('Blocks AGT-2793');
    expect(open.decidedBy).toBeNull();
    const settled = buildDecisionBadge(job({ kind: 'decision', decision: decision({ status: 'decided', decidedBy: 'role:lead' }) }))!;
    expect(settled).toMatchObject({ open: false, label: 'Decided', decider: 'Operator', decidedBy: 'Lead' });
    expect(settled.tooltip).toContain('decided by Lead');
  });

  it('falls back to the assigned decider when a settled record names nobody', () => {
    for (const decidedBy of [null, undefined, '', '  ']) {
      const settled = buildDecisionBadge(job({
        kind: 'decision',
        decision: decision({ status: 'decided', decider: 'role:lead', decidedBy }),
      }))!;
      expect(settled.decidedBy).toBe('Lead');
      expect(settled.tooltip).toBe('Decision card: decided by Lead.');
    }
  });

  it('finds the recommended option case-insensitively', () => {
    expect(recommendedOption(decision({ recommendedOptionId: ' A ' }))?.id).toBe('a');
    expect(recommendedOption(decision({ recommendedOptionId: 'z' }))).toBeNull();
    expect(recommendedOption(decision({ recommendedOptionId: null }))).toBeNull();
  });

  it('takes blockers from blockedBy and falls back to pendingDecision waits-on edges', () => {
    expect(decisionBlockers(job({ blockedBy: ['AGT-9'] }))).toEqual(['AGT-9']);
    expect(decisionBlockers(job({
      blockedBy: [],
      waitsOn: { blocked: true, cycleDetected: false, items: [
        { key: 'AGT-9', resolved: true, fulfilled: false, pendingDecision: true },
        { key: 'AGT-10', resolved: true, fulfilled: false },
      ] },
    }))).toEqual(['AGT-9']);
    expect(decisionBlockers(job())).toEqual([]);
  });

  it('explains the block the way the backend lane guard refuses it', () => {
    expect(decisionBlockReason([])).toBeNull();
    expect(decisionBlockReason(['AGT-9'])).toContain('Blocked by pending decision AGT-9');
    expect(decisionBlockReason(['AGT-9', 'AGT-10'])).toContain('pending decisions AGT-9, AGT-10');
  });

  it('mirrors DecisionLaneGuard for moves', () => {
    const blocked = job({ blockedBy: ['AGT-9'] });
    expect(decisionMoveRefusal(blocked, '2-ready')).toContain('AGT-9');
    expect(decisionMoveRefusal(blocked, '3-progress')).toContain('AGT-9');
    expect(decisionMoveRefusal(blocked, '0-backlog')).toBeNull();
    expect(decisionMoveRefusal(job(), '2-ready')).toBeNull();

    const pending = job({ kind: 'decision', decision: decision() });
    expect(decisionMoveRefusal(pending, '2-ready')).toContain('never enter a runner lane');
    expect(decisionMoveRefusal(pending, '6-completed')).toContain('Choose an option first');
    expect(decisionMoveRefusal(pending, '1-preparation')).toBeNull();

    const decided = job({ kind: 'decision', state: '6-completed', decision: decision({ status: 'decided' }) });
    expect(decisionMoveRefusal(decided, '7-archive')).toBeNull();
    expect(decisionMoveRefusal(decided, '1-preparation')).toContain('Reopen decision');
  });

  it('counts only open decision cards', () => {
    const jobs = [
      job({ id: 'a', kind: 'decision', decision: decision() }),
      job({ id: 'b', kind: 'decision', decision: decision({ status: 'decided' }) }),
      job({ id: 'c', kind: 'task' }),
    ];
    expect(openDecisionCards(jobs).map((j) => j.id)).toEqual(['a']);
  });

  it('flags an open decision past its due date as overdue', () => {
    const now = Date.parse('2026-09-20T00:00:00Z');
    expect(isDecisionOverdue(decision({ dueDate: '2026-09-16T00:00:00Z' }), now)).toBe(true);
    expect(isDecisionOverdue(decision({ dueDate: '2026-09-26T00:00:00Z' }), now)).toBe(false);
    expect(isDecisionOverdue(decision({ status: 'decided', dueDate: '2026-09-16T00:00:00Z' }), now)).toBe(false);
  });

  it('lists a project\'s pending decisions for the inbox, overdue first', () => {
    const now = Date.parse('2026-09-20T00:00:00Z');
    const items = buildDecisionInboxItems([
      job({ id: 'late', key: 'AGT-3', taskKey: 'p::late', createdAt: '2026-09-15T00:00:00Z', kind: 'decision', decision: decision({ dueDate: '2026-09-18T00:00:00Z' }) }),
      job({ id: 'early', key: 'AGT-2', taskKey: 'p::early', createdAt: '2026-09-10T00:00:00Z', kind: 'decision', decision: decision({ decider: 'role:reviewer' }) }),
      job({ id: 'other', key: 'OTH-1', projectName: 'Q', kind: 'decision', decision: decision() }),
      job({ id: 'done', key: 'AGT-4', kind: 'decision', decision: decision({ status: 'decided' }) }),
    ], 'P', undefined, now);
    expect(items.map((i) => i.key)).toEqual(['AGT-3', 'AGT-2']);
    expect(items[0]).toMatchObject({ overdue: true, taskKey: 'p::late', blocks: ['AGT-2793'] });
    expect(items[1].decider).toBe('Reviewer');
  });
});
