import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { DatePipe } from '@angular/common';
import { PendingButtonDirective } from '@studio/ui/components/async-feedback/index';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { formatAge } from '../../state/deploy-area.model';
import { runtimeHealth, type RollbackOffer } from '../../state/rollback-offer.model';

function ago(age: string | null): string | null {
  return age && age !== 'just now' ? `${age} ago` : age;
}

/**
 * The rollback entry point of one environment (F13): the target release, its
 * age and runtime health, and the one button that opens the confirmation.
 * The environment card, the run drawer, the incident card and the deployment
 * tools all show this same block.
 */
@Component({
  selector: 'app-rollback-offer',
  standalone: true,
  imports: [DatePipe, PendingButtonDirective, StatusIndicatorComponent],
  templateUrl: './rollback-offer.html',
  styleUrl: './rollback-offer.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RollbackOfferComponent {
  readonly offer = input.required<RollbackOffer>();
  readonly now = input.required<number>();
  readonly busy = input(false);
  readonly rollback = output<string>();

  readonly health = computed(() => runtimeHealth(this.offer().health));
  readonly age = computed(() => ago(formatAge(this.offer().targetCreatedAt, this.now())));
  readonly skipped = computed(() => this.offer().skippedVersions.map(version => `Release ${version}`).join(', '));
}
