import { describe, expect, it } from 'vitest';
import { buildGitStateBadge, gitContextBranch } from './task-card-view-model';
import type { TaskInfo } from '../../../../models/task.model';

/**
 * AGT-3043: the board card leaves out `gitContext.branch` when it is the
 * default `task/{id}` branch, which only repeats the (often long) task id.
 */
describe('gitContextBranch', () => {
  it('reads an absent branch on a present context as the default task branch', () => {
    expect(gitContextBranch({ id: 'long-task-id', gitContext: { branchTip: 'abc1234' } })).toBe('task/long-task-id');
  });

  it('keeps a non-default branch and an empty one as sent', () => {
    expect(gitContextBranch({ id: 'a', gitContext: { branch: 'runner/host/AGT-1' } })).toBe('runner/host/AGT-1');
    expect(gitContextBranch({ id: 'a', gitContext: { branch: '' } })).toBeNull();
  });

  it('is null without a git context', () => {
    expect(gitContextBranch({ id: 'a', gitContext: null })).toBeNull();
  });

  it('names the restored default branch in the pre-merge git-state tooltip', () => {
    const job = {
      id: 'long-task-id', key: 'AGT-9', state: '3-progress', title: 'Running',
      gitContext: { branchTip: 'abc1234def' },
    } as TaskInfo;
    expect(buildGitStateBadge(job)?.tooltip).toContain('task/long-task-id');
  });
});
