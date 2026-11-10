import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import type { TriageButton } from '../../../task-detail/state/triage-actions.model';
import { BoardMutationsService } from '../../state/board-mutations.service';
import { bulkLaneOffer, bulkTargetState } from '../../state/bulk-lane-actions';

/**
 * AGT-3585: the actions a group of cards can take together (an escalation
 * burst on the board or in the Activity feed). Offers only what is valid for
 * every member; each action asks for a confirmation that lists the cards.
 */
@Component({
  selector: 'app-bulk-lane-actions',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './bulk-lane-actions.component.html',
  styleUrl: './bulk-lane-actions.component.scss',
})
export class BulkLaneActionsComponent {
  private readonly mutations = inject(BoardMutationsService);

  readonly tasks = input.required<readonly TaskInfo[]>();
  /** Group members that are no longer on the board. */
  readonly missing = input(0);
  /** Say why nothing is offered (detail panes); lane headers stay quiet. */
  readonly showReason = input(false);
  readonly mutationsBlocked = input(false);

  readonly offer = computed(() => bulkLaneOffer(this.tasks(), this.missing()));
  readonly busy = this.mutations.bulkMoving;

  run(action: TriageButton): void {
    void this.mutations.bulkMove(this.tasks(), bulkTargetState(action), action.label);
  }
}
