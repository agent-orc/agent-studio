import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { councilFindingAction, councilReactionDisposition } from '../../../../../models/council-review.model';
import type { TaskInfo } from '../../../../../models/task.model';
import type { CouncilReviewReaction } from '../../../../../services/task.service';
import { taskNavigationHref } from '../../../state/task-url';

@Component({
  selector: 'app-council-review-reaction',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './council-review-reaction.component.html',
  styleUrl: './council-review-reaction.component.scss',
})
export class CouncilReviewReactionComponent {
  readonly job = input.required<TaskInfo>();
  readonly reaction = input<CouncilReviewReaction | null | undefined>(null);

  readonly disposition = computed(() => {
    const reaction = this.reaction();
    if (!reaction) return 'missing';
    return councilReactionDisposition(reaction.disposition) ?? reaction.disposition.toLowerCase();
  });
  readonly roundHref = computed(() => {
    const reaction = this.reaction();
    return reaction?.startsNewRound && reaction.targetJobId
      ? taskNavigationHref(this.job())
      : null;
  });

  actionLabel(action: string): string {
    switch (councilFindingAction(action)) {
      case 'fixNextRound': return 'Fix next round';
      case 'escalate': return 'Escalate';
      case 'accept': return 'Accept';
      default: return action;
    }
  }
}
