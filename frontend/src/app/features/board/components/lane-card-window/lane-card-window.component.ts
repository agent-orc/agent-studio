import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { TaskCardComponent } from '../task-card/task-card.component';
import type { LaneCardWindowView } from '../task-column/lane-card-window';

@Component({
  selector: 'app-lane-card-window',
  standalone: true,
  imports: [TaskCardComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './lane-card-window.html',
  styleUrl: './lane-card-window.scss',
})
export class LaneCardWindowComponent {
  readonly window = input.required<LaneCardWindowView>();
  readonly count = input.required<number>();
  readonly review = input(false);
  readonly compact = input(false);
  readonly highlightJobId = input<string | null>(null);
  readonly mutationsBlocked = input(false);
  readonly reorderDisabled = input(false);
  readonly dropIndex = input(-1);
  readonly jobClick = output<TaskInfo>();
  readonly deleteRequested = output<TaskInfo>();
  readonly pickNextRequested = output<TaskInfo>();
  readonly dragStarted = output<{ event: DragEvent; job: TaskInfo }>();
  readonly slotDragOver = output<{ event: DragEvent; index: number }>();
  readonly slotDragLeave = output<void>();
  readonly slotDrop = output<{ event: DragEvent; index: number }>();
}
