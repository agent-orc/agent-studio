import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import { bulkLaneOffer, bulkTargetState } from './bulk-lane-actions';

const card = (id: string, state = '5e-escalated', extra: Partial<TaskInfo> = {}) =>
  ({ id, taskKey: `demo::${id}`, title: id, state, watchPath: '/tmp/demo', ...extra }) as TaskInfo;

describe('bulkLaneOffer (AGT-3585)', () => {
  it('offers requeue and manual resolve for escalated cards, never accept or abort', () => {
    const offer = bulkLaneOffer([card('a'), card('b')]);
    expect(offer.reason).toBeNull();
    expect(offer.actions.map(action => [action.id, bulkTargetState(action)])).toEqual([
      ['reissue-escalated', '2-ready'],
      ['manual-resolve-escalated', '5-human-review'],
    ]);
  });

  it('withholds every action when a member left the board or the lanes differ', () => {
    expect(bulkLaneOffer([card('a')], 2)).toEqual({
      actions: [],
      reason: '2 of these cards are no longer on the board; open the rest one by one.',
    });
    expect(bulkLaneOffer([card('a'), card('b', '2-ready')]).actions).toEqual([]);
    expect(bulkLaneOffer([card('a', '2-ready'), card('b', '2-ready')]).reason).toBe('No bulk action applies in Ready.');
    expect(bulkLaneOffer([]).actions).toEqual([]);
  });

  it('drops an action the decision guard refuses for any member', () => {
    const decision = card('decision', '5e-escalated', { kind: 'decision' } as Partial<TaskInfo>);
    expect(bulkLaneOffer([card('a'), decision])).toEqual({
      actions: [],
      reason: 'No action is valid for every card in this group.',
    });
  });
});
