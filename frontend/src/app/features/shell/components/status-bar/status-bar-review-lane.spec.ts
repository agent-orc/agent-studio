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
  it('does not count unclassified Post Processing cards as review waits', () => {
    const lane = summarizeStatusBarReviewLane(Array.from({ length: 10 }, (_, i) => card(`DEMO-${i}`)));
    expect(lane).toEqual({ total: 10, reviewing: 0, waiting: 0, stalled: 0, integration: 0 });
  });

  it('splits review, integration and other cards and keeps the lane sum', () => {
    const lane = summarizeStatusBarReviewLane([
      card('A', { queue: { kind: 'review', state: 'reviewing', executorId: 'review-01' } }),
      card('B', { activeStep: { stepId: 'post-review', displayName: 'Review', kind: 'aspect' } }),
      card('C', { queue: { kind: 'review', position: 1 } }),
      card('D', { queue: { kind: 'review', state: 'stalled', reason: 'no review attempt' } }),
      card('E', { queue: { kind: 'review', state: 'integration', reason: 'waiting for delivery integration' } }),
      card('F'),
    ]);
    expect(lane).toEqual({ total: 6, reviewing: 2, waiting: 1, stalled: 1, integration: 1 });
    const unclassified = lane.total - lane.reviewing - lane.waiting - lane.stalled - lane.integration;
    expect(lane.reviewing + lane.waiting + lane.stalled + lane.integration + unclassified).toBe(lane.total);
    expect(describeStatusBarReviewLane(lane)).toBe(
      'Post Processing holds 6 cards: 1 waiting for review, 2 under review, 1 stalled, operator decision needed, 1 waiting for integration, 1 in post-processing.');
  });

  it('describes an empty lane', () => {
    expect(describeStatusBarReviewLane(summarizeStatusBarReviewLane([]))).toBe('Post Processing is empty.');
  });
});
