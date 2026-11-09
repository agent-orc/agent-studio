import { describe, expect, it } from 'vitest';
import type { TaskInfo, TaskLiveStatus } from '../../../../models/task.model';
import { describeStatusBarReviewLane, summarizeStatusBarReviewLane } from './status-bar-review-lane';

function card(id: string, liveStatus: Partial<TaskLiveStatus> | null = null): TaskInfo {
  return {
    id, taskKey: id, title: id, state: '4-auto-review', order: 0, agent: '', createdAt: '',
    watchPath: '', projectName: 'demo', folderPath: '', lastActivity: '', sessionName: null,
    model: '', cliType: 'codex', useOwnSession: null, lastUsage: null, execution: null, commit: null,
    liveStatus: liveStatus
      ? { attempt: 1, activeStep: null, nextSteps: [], queue: null, latestEventAt: null, ...liveStatus }
      : null,
  } as TaskInfo;
}

describe('summarizeStatusBarReviewLane', () => {
  it('counts every Post Processing card nothing reviews as waiting', () => {
    const lane = summarizeStatusBarReviewLane(Array.from({ length: 10 }, (_, i) => card(`DEMO-${i}`)));
    expect(lane).toEqual({ total: 10, reviewing: 0, waiting: 10, stalled: 0 });
  });

  it('splits reviewing, waiting and stalled cards and keeps the lane sum', () => {
    const lane = summarizeStatusBarReviewLane([
      card('A', { queue: { kind: 'review', state: 'reviewing', executorId: 'review-01' } }),
      card('B', { activeStep: { stepId: 'post-review', displayName: 'Review', kind: 'aspect' } }),
      card('C', { queue: { kind: 'review', position: 1 } }),
      card('D', { queue: { kind: 'review', state: 'stalled', reason: 'no review attempt' } }),
      card('E'),
    ]);
    expect(lane).toEqual({ total: 5, reviewing: 2, waiting: 2, stalled: 1 });
    expect(lane.reviewing + lane.waiting + lane.stalled).toBe(lane.total);
    expect(describeStatusBarReviewLane(lane)).toBe(
      'Post Processing holds 5 cards: 2 waiting for review, 2 under review, 1 stalled, operator decision needed.');
  });

  it('describes an empty lane', () => {
    expect(describeStatusBarReviewLane(summarizeStatusBarReviewLane([]))).toBe('Post Processing is empty.');
  });
});
