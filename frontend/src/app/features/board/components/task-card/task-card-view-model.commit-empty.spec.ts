import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { buildCommitEmptyBadge } from './task-card-view-model';

function card(over: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'AGT-2798',
    state: '5-human-review',
    commits: [],
    codeActivityDetected: false,
    integrationRecords: [],
    ...over,
  } as unknown as TaskInfo;
}

describe('buildCommitEmptyBadge (AGT-3045)', () => {
  it('keeps "no code changes" with no commits, no record, and no activity', () => {
    expect(buildCommitEmptyBadge(card())?.label).toBe('no code changes');
  });

  it('shows "integrated via operator merge" when an integrated record exists', () => {
    const badge = buildCommitEmptyBadge(card({
      integrationRecords: [{
        id: 'operator-merge-2026-09-15-agt-2798',
        classification: 'integrated-verified',
        integrationBranch: 'develop',
        commitShas: [],
        evidence: 'Operator card-scoped merge 5d480157f.',
      }],
    }));
    expect(badge?.tone).toBe('integrated');
    expect(badge?.label).toBe('integrated via operator merge');
    expect(badge?.tooltip).toContain('Record: operator-merge-2026-09-15-agt-2798');
    expect(badge?.tooltip).toContain('Branch: develop');
  });

  it('ignores records that do not prove integration', () => {
    const badge = buildCommitEmptyBadge(card({
      integrationRecords: [{ id: 'sweep', classification: 'genuinely-missing' }],
    }));
    expect(badge?.label).toBe('no code changes');
  });
});
