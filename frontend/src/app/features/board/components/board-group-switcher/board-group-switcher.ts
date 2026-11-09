import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { SegmentedControlComponent, type SegmentedOption } from '../../../../components/segmented-control/segmented-control.component';
import { laneName } from '../../../../models/lane-presentation';
import { TaskState } from '../../../../models/task.model';
import {
  countedLanes,
  laneGroupCount,
  type BoardDeliveryGroup,
  type BoardLaneGroup,
} from '../board-lane-layout.util';

/**
 * Narrow-board group switcher (AGT-3590). Below tablet width the studio board
 * shows one container at a time; this strip picks which one and carries each
 * container's card count, so the counts of the hidden containers stay visible.
 */
@Component({
  selector: 'app-board-group-switcher',
  standalone: true,
  imports: [SegmentedControlComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './board-group-switcher.html',
  styleUrl: './board-group-switcher.scss',
})
export class BoardGroupSwitcherComponent {
  readonly groups = input.required<readonly BoardLaneGroup[]>();
  /** The Delivery group while it shows on the board, else null. */
  readonly delivery = input<BoardDeliveryGroup | null>(null);
  readonly value = input<string | null>(null);
  readonly valueChange = output<string>();

  readonly options = computed(() => boardGroupOptions(this.groups(), this.delivery()));
}

/** One switcher choice per container: label plus count, with the summed lanes on hover. */
export function boardGroupOptions(
  groups: readonly BoardLaneGroup[],
  delivery: BoardDeliveryGroup | null,
): SegmentedOption[] {
  const options = groups.map((group): SegmentedOption => {
    const count = laneGroupCount(group);
    const lanes = countedLanes(group).map((lane) => lane.title).join(', ');
    const archived = group.lanes.some((lane) => lane.state === TaskState.Archive)
      ? ` ${laneName(TaskState.Archive)} is not counted.`
      : '';
    return {
      value: group.id,
      label: `${group.label} ${count}`,
      testid: `board-group-switch-${group.id}`,
      tooltip: `${taskCount(count)} in ${lanes}.${archived}`,
    };
  });
  if (delivery) {
    options.push({
      value: delivery.id,
      label: `${delivery.label} ${delivery.count}`,
      testid: `board-group-switch-${delivery.id}`,
      tooltip: `${taskCount(delivery.count)} across ${delivery.columns.length} delivery columns.`,
    });
  }
  return options;
}

function taskCount(count: number): string {
  return `${count} ${count === 1 ? 'task' : 'tasks'}`;
}
