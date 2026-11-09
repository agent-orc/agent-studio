/**
 * AGT-3590: below tablet width the board shows exactly one container; wide
 * boards keep the optional container focus.
 */
import { beforeEach, describe, expect, it } from 'vitest';
import { LaneCollapseService } from './lane-collapse.service';

const ids = ['backlog', 'active', 'decide'];

describe('LaneCollapseService.boardFocus', () => {
  let service: LaneCollapseService;
  beforeEach(() => {
    service = new LaneCollapseService();
  });

  it('shows every container on a wide board until one is focused', () => {
    service.singleGroupLayout.set(false);
    expect(service.boardFocus(ids)).toBeNull();
    service.toggleContainerFocus('active', ids);
    expect(service.boardFocus(ids)).toBe('active');
  });

  it('shows the first container below tablet width when nothing is picked', () => {
    service.singleGroupLayout.set(true);
    expect(service.boardFocus(ids)).toBe('backlog');
  });

  it('shows the switcher pick below tablet width without focusing the wide board', () => {
    service.singleGroupLayout.set(true);
    service.selectSingleGroup('decide');
    expect(service.boardFocus(ids)).toBe('decide');
    service.singleGroupLayout.set(false);
    expect(service.boardFocus(ids)).toBeNull();
  });

  it('falls back to the first container when the pick is no longer on the board', () => {
    service.singleGroupLayout.set(true);
    service.selectSingleGroup('delivery');
    expect(service.boardFocus([...ids, 'delivery'])).toBe('delivery');
    expect(service.boardFocus(ids)).toBe('backlog');
  });
});
