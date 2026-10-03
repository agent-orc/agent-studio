import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { decisionBlockers } from '../../../../models/decision-card-presentation';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';

interface BlockingDecision {
  key: string;
  title: string | null;
}

/**
 * AGT-2795: a card whose `dependsOn` names a pending decision card says so at
 * the top of its detail, links the decision, and states that the claim guard
 * keeps it out of Ready and In Progress until the decider chooses.
 */
@Component({
  selector: 'app-decision-blocked-notice',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './decision-blocked-notice.component.html',
  styleUrl: './decision-blocked-notice.component.scss',
})
export class DecisionBlockedNoticeComponent {
  private readonly taskNavigation = inject(TaskReferenceNavigationService);

  readonly job = input.required<TaskInfo>();

  readonly blockers = computed<BlockingDecision[]>(() => {
    const job = this.job();
    return decisionBlockers(job).map((key) => ({
      key,
      title: job.waitsOn?.items.find((item) => item.key.toUpperCase() === key.toUpperCase())?.targetTitle ?? null,
    }));
  });

  open(key: string): void {
    this.taskNavigation.openReferenceOrNotify(key);
  }
}
