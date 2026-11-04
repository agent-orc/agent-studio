import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { ReviewAttemptCycle } from '../../../../../../features/run-timeline';
import { formatDateTime } from '../../../../../../services/format.util';
import { StatusIndicatorComponent } from '../../../../../../components/status-indicator/status-indicator.component';

@Component({
  selector: 'app-review-attempt-history',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './review-attempt-history.component.html',
  styleUrl: './review-attempt-history.component.scss',
})
export class ReviewAttemptHistoryComponent {
  readonly currentEpoch = input(0);
  readonly cycles = input<ReviewAttemptCycle[]>([]);

  readonly visibleCycles = computed<ReviewAttemptCycle[]>(() => {
    const currentEpoch = Math.max(0, this.currentEpoch());
    const supplied = this.cycles()
      .filter(cycle => cycle.epoch >= 0 && cycle.epoch <= currentEpoch)
      .sort((left, right) => right.epoch - left.epoch);
    if (supplied.length > 0) return supplied;
    return [{
      epoch: currentEpoch,
      isCurrent: true,
      startedAt: null,
      endedAt: null,
      actor: null,
      reason: currentEpoch === 0 ? 'Initial review cycle.' : null,
      fromState: null,
      toState: null,
      rotatedArtifacts: 0,
    }];
  });

  cycleLabel(cycle: ReviewAttemptCycle): string {
    return cycle.isCurrent ? 'Current' : 'Closed';
  }

  formatWhen(value: string | null): string {
    return value ? formatDateTime(value) : 'time not recorded';
  }

  laneTransition(cycle: ReviewAttemptCycle): string | null {
    if (!cycle.fromState && !cycle.toState) return null;
    return `${cycle.fromState ?? 'unknown'} → ${cycle.toState ?? 'unknown'}`;
  }
}
