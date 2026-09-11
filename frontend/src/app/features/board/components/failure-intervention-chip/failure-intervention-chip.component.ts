import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TaskState, type TaskInfo } from '../../../../models/task.model';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';
import { stateLabel } from '../../../../services/format.util';
import { TaskSelectionService } from '../../../task-detail';

@Component({
  selector: 'app-failure-intervention-chip',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './failure-intervention-chip.component.html',
  styleUrl: './failure-intervention-chip.component.scss',
})
export class FailureInterventionChipComponent {
  readonly origin = input.required<TaskInfo>();
  private readonly tasks = inject(TaskService);
  private readonly selection = inject(TaskSelectionService);
  private readonly notifications = inject(NotificationService);

  readonly followUp = computed(() => {
    const key = this.origin().references?.raisedFollowUps?.[0];
    if (!key) return null;
    const target = this.tasks.jobs().find(task =>
      task.key?.localeCompare(key, undefined, { sensitivity: 'accent' }) === 0);
    return {
      key,
      lane: target ? stateLabel(target.state) : 'not loaded',
      state: target && (target.state === TaskState.Completed || target.state === TaskState.Archive)
        ? 'closed'
        : 'open',
      target,
    };
  });

  navigate(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    const followUp = this.followUp();
    if (followUp?.target) this.selection.openDetail(followUp.target);
    else if (followUp) this.notifications.info(`${followUp.key} is not loaded in the current workspace view.`);
  }
}
