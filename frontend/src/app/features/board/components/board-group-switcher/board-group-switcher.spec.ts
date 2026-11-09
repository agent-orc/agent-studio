/**
 * AGT-3590: the narrow-board switcher names each container with its card
 * count and picks the container the board shows alone.
 */
import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { BoardGroupSwitcherComponent, boardGroupOptions } from './board-group-switcher';
import { buildLaneGroups, type BoardDeliveryGroup } from '../board-lane-layout.util';
import { laneName } from '../../../../models/lane-presentation';
import { GroupedJobs, TaskInfo, TaskState } from '../../../../models/task.model';

function task(id: string, state: string): TaskInfo {
  return { id, taskKey: `ws::${id}`, title: id, state, order: 1, projectName: 'demo' } as TaskInfo;
}

const grouped: GroupedJobs = {
  backlog: [task('b1', TaskState.Backlog)], preparation: [], orchestratorPrep: [],
  ready: [task('r1', TaskState.Ready), task('r2', TaskState.Ready)], progress: [task('p1', TaskState.Progress)],
  failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [task('h1', TaskState.HumanReview)],
  escalated: [], review: [], completed: [], archive: [task('a1', TaskState.Archive)],
};
const groups = buildLaneGroups(grouped, false);
const delivery: BoardDeliveryGroup = { id: 'delivery', label: 'Delivery', columns: [], count: 4 };

describe('boardGroupOptions', () => {
  it('labels every container with its card count', () => {
    expect(boardGroupOptions(groups, null).map((option) => [option.value, option.label])).toEqual([
      ['backlog', 'Backlog 3'],
      ['active', 'Active 1'],
      ['decide', 'Done & Decide 1'],
    ]);
  });

  it('names the summed lanes on hover and says the Archive is not counted', () => {
    const decide = boardGroupOptions(groups, null).find((option) => option.value === 'decide')!;
    expect(decide.tooltip).toContain(laneName(TaskState.HumanReview));
    expect(decide.tooltip).toContain(`${laneName(TaskState.Archive)} is not counted.`);
    expect(boardGroupOptions(groups, null)[0].tooltip).toBe(
      `3 tasks in ${[TaskState.Ready, TaskState.Preparation, TaskState.Backlog].map(laneName).join(', ')}.`,
    );
  });

  it('adds the Delivery group only while it shows on the board', () => {
    const options = boardGroupOptions(groups, delivery);
    expect(options.at(-1)).toMatchObject({ value: 'delivery', label: 'Delivery 4', testid: 'board-group-switch-delivery' });
  });
});

describe('BoardGroupSwitcherComponent', () => {
  function setup(value: string) {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(BoardGroupSwitcherComponent);
    fixture.componentRef.setInput('groups', groups);
    fixture.componentRef.setInput('value', value);
    fixture.detectChanges();
    return fixture;
  }

  it('marks the shown container pressed and emits a picked one', () => {
    const fixture = setup('active');
    const host = fixture.nativeElement as HTMLElement;
    const pressed = host.querySelector('[aria-pressed="true"]');
    expect(pressed?.getAttribute('data-testid')).toBe('board-group-switch-active');
    const picked: string[] = [];
    fixture.componentInstance.valueChange.subscribe((id) => picked.push(id));
    host.querySelector<HTMLButtonElement>('[data-testid="board-group-switch-decide"]')!.click();
    expect(picked).toEqual(['decide']);
  });
});
