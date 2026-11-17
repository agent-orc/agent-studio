import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { AcceptanceReadinessStore } from '../../../acceptance-readiness';
import { acceptanceRefusalConcern } from '../../state/acceptance-refusal.model';
import { AcceptanceRefusalActions, OVERRIDE_ACTION_LABEL, REVIEW_AGAIN_LABEL } from '../../state/acceptance-refusal-actions.service';
import type { TaskMoveRefusal } from '../../state/triage-controller.service';

/**
 * The banner above a card whose move was refused. AGT-3873: for a content
 * refusal it names the review's concern itself and offers the two steps that
 * resolve it, the written-reason override (the toolbar form) and a review
 * against the current brief, instead of only naming them.
 */
@Component({
  selector: 'app-task-move-refusal',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-move-refusal.component.html',
  styleUrl: './task-move-refusal.component.scss',
})
export class TaskMoveRefusalComponent {
  private readonly actions = inject(AcceptanceRefusalActions);
  private readonly readiness = inject(AcceptanceReadinessStore);

  readonly refusal = input.required<TaskMoveRefusal>();
  readonly job = input.required<TaskInfo>();
  readonly mutationsBlocked = input(false);

  readonly overrideLabel = OVERRIDE_ACTION_LABEL;
  readonly reviewLabel = REVIEW_AGAIN_LABEL;

  /** The concern of an overridable content refusal; the dry run fills it in after a reload. */
  readonly concern = computed(() => this.refusal().overridable
    ? acceptanceRefusalConcern(this.job(), this.readiness.itemFor(this.job()))
    : null);

  override(): void {
    this.actions.openOverride(this.job());
  }

  reviewAgain(): void {
    this.actions.reviewAgain(this.job());
  }
}
