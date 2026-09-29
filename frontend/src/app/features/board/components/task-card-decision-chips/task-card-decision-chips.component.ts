import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskInfo } from '../../../../models/task.model';
import { buildDecisionBadge, decisionBlockReason, decisionBlockers } from '../../../../models/decision-card-presentation';
import { ClientService } from '../../../../services/client.service';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';
import { TaskSelectionService } from '../../../task-detail/runtime';
import { resolveDependencyTarget } from '../task-card/task-card-view-model';

/**
 * AGT-2795: the decision chips in a board card's badge row. On a decision card
 * it names the decider; on a card waiting on a pending decision it renders one
 * "blocked by AGT-nnnn" link per blocker that opens the decision card.
 */
@Component({
  selector: 'app-task-card-decision-chips',
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './task-card-decision-chips.component.html',
  styleUrl: './task-card-decision-chips.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskCardDecisionChipsComponent {
  private readonly clients = inject(ClientService);
  private readonly tasks = inject(TaskService);
  private readonly selection = inject(TaskSelectionService);
  private readonly notifications = inject(NotificationService);

  readonly job = input.required<TaskInfo>();

  readonly badge = computed(() =>
    buildDecisionBadge(this.job(), (id) => this.clients.byId().get(id)?.displayName));
  readonly blockers = computed(() => decisionBlockers(this.job()));
  readonly blockTooltip = computed(() => decisionBlockReason(this.blockers()));

  open(key: string, event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    const edge = this.job().waitsOn?.items.find((item) => item.key.toUpperCase() === key.toUpperCase());
    const target = resolveDependencyTarget({
      glyph: '', label: '', tone: 'open', tooltip: '', targetKey: key,
      targetJobId: edge?.targetJobId ?? null, targetWatchPath: edge?.targetWatchPath ?? null,
    }, this.tasks.jobs());
    if (target) {
      this.selection.openDetail(target);
      return;
    }
    this.notifications.info(`${key} is not loaded in the current workspace view.`);
  }
}
