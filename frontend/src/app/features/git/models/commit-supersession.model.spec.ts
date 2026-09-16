import { describe, expect, it } from 'vitest';
import type { TaskCommitInfo } from './git.model';
import {
  PENDING_ATTEMPT,
  commitSupersessionState,
  isEffectiveDelivery,
  isReplacedCommit,
} from './commit-supersession.model';

function commit(overrides: Partial<TaskCommitInfo> = {}): TaskCommitInfo {
  return {
    sha: '79c2dcf8c4b1f2a3d5e6f7a8b9c0d1e2f3a4b5c6',
    shortSha: '79c2dcf8c',
    message: 'feat(archive): retention policy',
    filesChanged: 17,
    files: [],
    at: '2026-09-07T21:31:00Z',
    ...overrides,
  } as TaskCommitInfo;
}

describe('commitSupersessionState: pending is not a verdict', () => {
  it('reads an unmarked commit as the live delivery', () => {
    expect(commitSupersessionState(commit())).toBe('current');
    expect(isEffectiveDelivery(commit())).toBe(true);
  });

  /**
   * AGT-2706: integrated by an operator card-scoped merge, so no replacement
   * attempt ever published and the placeholder was never resolved. Reading it
   * as a replacement showed the shipped delivery as replaced.
   */
  it('keeps the placeholder in the current delivery, not in replaced history', () => {
    const pending = commit({ supersededByAttempt: PENDING_ATTEMPT });

    expect(commitSupersessionState(pending)).toBe('replacement-pending');
    expect(isReplacedCommit(pending)).toBe(false);
    expect(isEffectiveDelivery(pending)).toBe(true);
  });

  it('reads a resolved attempt id as replaced', () => {
    const replaced = commit({ supersededByAttempt: 'run_e1fbb2898c6a4e99a0fd5612b94104c2' });

    expect(commitSupersessionState(replaced)).toBe('replaced');
    expect(isEffectiveDelivery(replaced)).toBe(false);
  });

  it('lets a replacement SHA outrank a leftover placeholder', () => {
    const replaced = commit({
      supersededByAttempt: PENDING_ATTEMPT,
      supersededBySha: 'ca70d1877aa',
    });

    expect(commitSupersessionState(replaced)).toBe('replaced');
  });
});
