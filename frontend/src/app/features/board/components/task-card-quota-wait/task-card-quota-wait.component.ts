import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskInfo } from '../../../../models/task.model';
import { taskCardNow } from '../task-card/task-card-clock';
import { buildQuotaWaitBadge } from '../task-card/task-card-view-model';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';

@Component({
  selector: 'app-task-card-quota-wait',
  standalone: true,
  imports: [TooltipDirective, StatusIndicatorComponent],
  templateUrl: './task-card-quota-wait.component.html',
  styleUrl: './task-card-quota-wait.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskCardQuotaWaitComponent {
  readonly wait = input<TaskInfo['quotaWait']>(null);
  readonly badge = computed(() => buildQuotaWaitBadge(this.wait(), taskCardNow()));
}
