import { describe, expect, it } from 'vitest';
import { TaskState } from '../../../../models/task.model';
import type { TaskInfo, TaskReferenceLink } from '../../../../models/task.model';
import {
  canOfferRelease,
  dependentKey,
  isReleasableLane,
  releasableWaitsOnTargets,
  releaseGatedDependents,
} from './release-gate.model';

/**
 * AGT-2709 — the visibility matrix for the release affordance, tested directly
 * so "when may an operator release?" is pinned without a render harness.
 */
function link(overrides: Partial<TaskReferenceLink> = {}): TaskReferenceLink {
  return {
    sourceKey: 'AGT-2373',
    sourceJobId: 'dependent',
    sourceTitle: 'Dependent card',
    sourceState: TaskState.Ready,
    sourceWatchPath: '/ws/agt',
    kind: 'dependsOn',
    releaseGate: true,
    ...overrides,
  };
}

function task(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'target',
    taskKey: 'agt::target',
    key: 'AGT-2372',
    title: 'Release target',
    state: TaskState.Archive,
    watchPath: '/ws/agt',
    ...overrides,
  } as TaskInfo;
}

describe('release-gate rules', () => {
  it('treats only the two terminal lanes as releasable', () => {
    expect(isReleasableLane(TaskState.Completed)).toBe(true);
    expect(isReleasableLane(TaskState.Archive)).toBe(true);
    expect(isReleasableLane(TaskState.HumanReview)).toBe(false);
    expect(isReleasableLane(undefined)).toBe(false);
  });

  it('counts only release-gated dependsOn edges as dependents of the decision', () => {
    const dependents = [
      link(),
      link({ sourceKey: 'AGT-9', releaseGate: false }),
      link({ sourceKey: 'AGT-8', kind: 'relatedTo' }),
    ];
    expect(releaseGatedDependents(dependents).map(l => l.sourceKey)).toEqual(['AGT-2373']);
    expect(releaseGatedDependents(undefined)).toEqual([]);
  });

  it('offers the action only for a terminal task that actually gates a dependent', () => {
    expect(canOfferRelease(task(), [link()])).toBe(true);
    expect(canOfferRelease(task({ state: TaskState.Completed }), [link()])).toBe(true);
    // Not terminal yet: the flag would approve content that does not exist.
    expect(canOfferRelease(task({ state: TaskState.Progress }), [link()])).toBe(false);
    // Terminal, but nothing gates on it: a control that changes nothing.
    expect(canOfferRelease(task(), [link({ releaseGate: false })])).toBe(false);
    expect(canOfferRelease(task(), [])).toBe(false);
  });

  it('lists the waits-on targets that are complete but unreleased', () => {
    const dependent = task({
      state: TaskState.Ready,
      waitsOn: {
        blocked: true,
        cycleDetected: false,
        items: [
          { key: 'AGT-2372', resolved: true, fulfilled: false, waitingForRelease: true, targetJobId: 'target' },
          { key: 'AGT-1', resolved: true, fulfilled: false, waitingForRelease: false, targetJobId: 'other' },
          // Unresolved target: no folder id to address the release call to.
          { key: 'AGT-2', resolved: false, fulfilled: false, waitingForRelease: true, targetJobId: null },
        ],
      },
    });
    expect(releasableWaitsOnTargets(dependent).map(i => i.key)).toEqual(['AGT-2372']);
    expect(releasableWaitsOnTargets(task())).toEqual([]);
  });

  it('labels a dependent by key, falling back to the folder id when keyless', () => {
    expect(dependentKey(link())).toBe('AGT-2373');
    expect(dependentKey(link({ sourceKey: null }))).toBe('dependent');
  });
});
