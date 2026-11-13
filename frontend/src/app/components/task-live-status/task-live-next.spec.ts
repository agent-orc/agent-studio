import { describe, expect, it } from 'vitest';
import type { TaskInfo, TaskLiveStatus } from '../../models/task.model';
import { TaskState } from '../../models/task.model';
import { postProcessingWait } from './task-live-next';

const task = (state: TaskInfo['state']) => ({ state }) as TaskInfo;
const queue = (state: string, reason: string | null = null) => ({ state, reason }) as TaskLiveStatus['queue'];

describe('postProcessingWait', () => {
  it('names the review verdict and operator decision only in Post Processing', () => {
    expect(postProcessingWait(task(TaskState.AutoReview), queue('reviewing'), null))
      .toEqual({ kind: 'reviewing', action: 'Review verdict' });
    expect(postProcessingWait(task(TaskState.AutoReview), queue('stalled'), null))
      .toEqual({ kind: 'stalled', action: 'Operator decision' });
    expect(postProcessingWait(task(TaskState.Ready), queue('stalled'), null)).toBeNull();
  });

  it('prefers the batch action over the queue reason', () => {
    expect(postProcessingWait(task(TaskState.AutoReview), queue('waiting', 'waiting for operator action'), 'Batch verdict'))
      .toEqual({ kind: 'batch', action: 'Batch verdict' });
  });

  it('reads the workflow wait from the queue reason and falls back to review', () => {
    expect(postProcessingWait(task(TaskState.AutoReview), queue('waiting', 'Waiting for operator action'), null))
      .toEqual({ kind: 'workflow', action: 'Operator decision' });
    expect(postProcessingWait(task(TaskState.AutoReview), queue('waiting', 'batch gate'), null))
      .toEqual({ kind: 'workflow', action: 'Batch verdict' });
    expect(postProcessingWait(task(TaskState.AutoReview), null, null)).toEqual({ kind: 'review', action: 'Review' });
    expect(postProcessingWait(task(TaskState.HumanReview), null, null)).toBeNull();
  });
});
