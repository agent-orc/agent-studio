import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskInfo } from '../../../../models/task.model';
import { buildDecisionBadge, decisionBlockReason, decisionBlockers } from '../../../../models/decision-card-presentation';
import { ClientService } from '../../../../services/client.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** AGT-3403: an open decision waits for its decider (warning); a decided one is settled (R4). */
export function deciderTone(open: boolean): StatusTone {
  return open ? 'warning' : 'neutral';
}

/**
 * AGT-2795: the decision chips in a board card's badge row. On a decision card
 * it names the decider (or, once settled, who decided); on a card waiting on a
 * pending decision it renders one "blocked by AGT-nnnn" link per blocker that
 * opens the decision card.
 */
@Component({
  selector: 'app-task-card-decision-chips',
  standalone: true,
  imports: [TooltipDirective, StatusIndicatorComponent],
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
  readonly deciderTone = deciderTone;
  readonly blockers = computed(() => decisionBlockers(this.job()));
  readonly blockTooltip = computed(() => decisionBlockReason(this.blockers()));

  open(key: string, event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.taskNavigation.openReferenceOrNotify(key);
  }
}
