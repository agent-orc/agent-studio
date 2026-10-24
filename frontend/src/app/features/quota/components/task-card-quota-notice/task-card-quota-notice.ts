import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';

import { isTerminalTaskState, type TaskInfo } from '../../../../models/task.model';
import { WorkspaceQuotaBoardState } from '../../state/workspace-quota-board-state.service';

/** Card-level quota refusal: shown only on cards that still wait for admission (not running, not finished). */
@Component({
  selector: 'app-task-card-quota-notice',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-card-quota-notice.html',
  styleUrl: './task-card-quota-notice.scss',
})
export class TaskCardQuotaNoticeComponent {
  private readonly quotaBoard = inject(WorkspaceQuotaBoardState);
  readonly task = input.required<Pick<TaskInfo, 'projectName' | 'state'>>();
  readonly running = input(false);
  readonly message = computed(() => {
    const task = this.task();
    return this.running() || isTerminalTaskState(task.state) ? null : this.quotaBoard.messageFor(task.projectName);
  });
}
