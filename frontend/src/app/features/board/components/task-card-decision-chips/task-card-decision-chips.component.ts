import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskInfo } from '../../../../models/task.model';
import { buildDecisionBadge, decisionBlockReason, decisionBlockers } from '../../../../models/decision-card-presentation';
import { ClientService } from '../../../../services/client.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';

/**
 * AGT-2795: the decision chips in a board card's badge row. On a decision card
 * it names the decider (or, once settled, who decided); on a card waiting on a
 * pending decision it renders one "blocked by AGT-nnnn" link per blocker that
 * opens the decision card.
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
  private readonly taskNavigation = inject(TaskReferenceNavigationService);

  readonly job = input.required<TaskInfo>();

  readonly badge = computed(() =>
    buildDecisionBadge(this.job(), (id) => this.clients.byId().get(id)?.displayName));
  readonly blockers = computed(() => decisionBlockers(this.job()));
  readonly blockTooltip = computed(() => decisionBlockReason(this.blockers()));

  open(key: string, event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.taskNavigation.openReferenceOrNotify(key);
  }
}
