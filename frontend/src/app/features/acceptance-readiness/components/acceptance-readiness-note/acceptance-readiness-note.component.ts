import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TaskState, type TaskInfo } from '../../../../models/task.model';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { withRouteSegment } from '../../../../services/url-hash.util';
import { AcceptanceReadinessStore } from '../../state/acceptance-readiness.store';
import { readinessPresentation } from '../../models/acceptance-readiness.model';

/**
 * AGT-3564 - a Human Review card's acceptance readiness in plain words: the
 * class, the reason with the concern the review raised, a link to the review
 * report, and for a refused card the offer of a round that closes the gap.
 */
@Component({
  selector: 'app-acceptance-readiness-note',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './acceptance-readiness-note.html',
  styleUrl: './acceptance-readiness-note.scss',
})
export class AcceptanceReadinessNoteComponent {
  private readonly store = inject(AcceptanceReadinessStore);
  readonly job = input.required<TaskInfo>();

  readonly item = computed(() => this.job().state === TaskState.HumanReview ? this.store.itemFor(this.job()) : null);
  readonly tone = computed(() => readinessPresentation(this.item()?.readiness ?? 'blocked').tone);

  /** The card's review evidence tab, where the review report renders. */
  readonly reportHref = computed(() => {
    const reference = this.job().displayKey || this.job().key || this.job().id;
    const hash = typeof window === 'undefined' ? '' : window.location.hash;
    return withRouteSegment(hash, `/tasks/${encodeURIComponent(reference)}?view=evidence:protocol`);
  });

  stop(event: Event): void {
    event.stopPropagation();
  }

  openRound(event: Event): void {
    event.stopPropagation();
    this.store.openRound(this.job());
  }
}
