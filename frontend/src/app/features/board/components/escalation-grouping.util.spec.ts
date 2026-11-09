import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import { groupEscalatedJobs } from './escalation-grouping.util';

function escalated(id: string, blockerType: string | null, parkedAt = '2026-11-09T09:23:00Z'): TaskInfo {
  return {
    id,
    taskKey: `demo::${id}`,
    title: id,
    state: '5e-escalated',
    watchPath: '/tmp/demo',
    parkedBlocker: blockerType === null ? null : { blockerType, parkedAt } as TaskInfo['parkedBlocker'],
  } as TaskInfo;
}

describe('groupEscalatedJobs (AGT-3585)', () => {
  it('returns null for a calm lane so it keeps its flat list', () => {
    expect(groupEscalatedJobs([escalated('a', 'stall-intervention'), escalated('b', 'missing-delivery')])).toBeNull();
  });

  it('groups a burst by recorded cause and keeps every other card in one rest group (R3)', () => {
    const jobs = [
      escalated('a', 'stall-intervention'),
      escalated('b', 'missing-delivery'),
      escalated('c', 'stall-intervention', '2026-11-09T09:23:04Z'),
      escalated('d', 'missing-delivery', '2026-11-09T09:23:02Z'),
      escalated('e', 'missing-delivery', '2026-11-09T09:23:03Z'),
      escalated('f', null),
      escalated('g', 'stall-intervention', '2026-11-08T09:00:00Z'),
    ];
    const groups = groupEscalatedJobs(jobs)!;
    expect(groups.map(group => [group.kind, group.label, group.jobs.map(job => job.id)])).toEqual([
      ['burst', 'Stall Intervention', ['a', 'c']],
      ['burst', 'Missing Delivery', ['b', 'd', 'e']],
      ['rest', 'Other escalations', ['f', 'g']],
    ]);
    expect(groups[1].burst).toEqual({ cause: 'missing-delivery', since: '2026-11-09T09:23:00.000Z' });
    expect(groups.reduce((sum, group) => sum + group.jobs.length, 0)).toBe(jobs.length);
    expect(new Set(groups.map(group => group.key)).size).toBe(groups.length);
  });
});
