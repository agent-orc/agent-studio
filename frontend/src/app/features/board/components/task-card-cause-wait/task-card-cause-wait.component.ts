import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskInfo } from '../../../../models/task.model';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';
import { TaskSelectionService } from '../../../task-detail/runtime';
import { buildCauseWaitBadge } from '../task-card/task-card-view-model';

@Component({
  selector: 'app-task-card-cause-wait',
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './task-card-cause-wait.component.html',
  styleUrl: './task-card-cause-wait.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskCardCauseWaitComponent {
  readonly wait = input<TaskInfo['causeWait']>(null);
  readonly badge = computed(() => buildCauseWaitBadge(this.wait()));
  private readonly tasks = inject(TaskService);
  private readonly selection = inject(TaskSelectionService);
  private readonly notifications = inject(NotificationService);

  navigate(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    const key = this.badge()?.causeKey;
    if (!key) return;
    const target = this.tasks.jobs().find(task =>
      task.key?.localeCompare(key, undefined, { sensitivity: 'accent' }) === 0);
    if (target) this.selection.openDetail(target);
    else this.notifications.info(`${key} is not loaded in the current workspace view.`);
  }
}
