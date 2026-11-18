import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { DatePipe } from '@angular/common';
import {
  HEALTH_LABEL,
  RUN_STATE_LABEL,
  formatAge,
  shortArtifact,
  triggerLabel,
  type EnvironmentCardView,
} from '../../state/deploy-area.model';
import { healthStateTone, runStateTone } from '../../state/deploy-status-tone';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { RollbackOfferComponent } from '../rollback-offer/rollback-offer';
import type { RollbackOffer } from '../../state/rollback-offer.model';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { DeploySecretGapsComponent } from '../deploy-secret-gaps/deploy-secret-gaps';
import { runHostStateLabel, runHostStateTone } from '../../../../models/run-host.model';

function ago(age: string | null): string | null {
  return age && age !== 'just now' ? `${age} ago` : age;
}

/** Status card of one environment: release, age, cards, health, and the last run. */
@Component({
  selector: 'app-deploy-environment-card',
  standalone: true,
  imports: [DatePipe, StatusIndicatorComponent, RollbackOfferComponent, PendingButtonDirective, DeploySecretGapsComponent],
  templateUrl: './deploy-environment-card.html',
  styleUrl: './deploy-environment-card.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DeployEnvironmentCardComponent {
  readonly environment = input.required<EnvironmentCardView>();
  readonly now = input.required<number>();
  readonly busy = input(false);
  readonly openRun = output<string>();
  readonly openTools = output<void>();
  /** F13: the environment's rollback target, when it has one. */
  readonly rollbackOffer = input<RollbackOffer | null>(null);
  readonly rollbackBusy = input(false);
  readonly rollback = output<string>();
  readonly stop = output<string>();
  readonly remove = output<string>();
  /** F14: open the binding panel on this environment. */
  readonly openSecrets = output<string>();

  readonly headingId = computed(() => `deploy-environment-${this.environment().id}-title`);
  readonly release = computed(() => {
    const env = this.environment();
    if (env.releaseVersion) return `Release ${env.releaseVersion}`;
    return env.artifact ? shortArtifact(env.artifact) : null;
  });
  readonly age = computed(() => ago(formatAge(this.environment().deployedAt, this.now())));
  readonly cards = computed(() => {
    const cards = this.environment().cards;
    if (cards.status === 'unknown') return { known: false, text: cards.reason };
    return { known: true, text: cards.keys.length ? cards.keys.join(', ') : 'No cards in this release' };
  });
  readonly newer = computed(() => {
    const newer = this.environment().newer;
    if (!newer) return null;
    const count = newer.cardKeys.length;
    return `Release ${newer.version} is ready with ${count} ${count === 1 ? 'card' : 'cards'}`;
  });
  readonly runTone = runStateTone;
  readonly hostTone = runHostStateTone;
  readonly hostLabel = runHostStateLabel;
  readonly health = computed<{ tone: StatusTone; label: string }>(() => {
    const health = this.environment().health;
    if (health.status === 'no-checks') return { tone: 'neutral', label: 'No checks' };
    return { tone: healthStateTone(health.state), label: HEALTH_LABEL[health.state] };
  });
  readonly healthProblem = computed(() => {
    const health = this.environment().health;
    return health.status === 'reported' ? health.problem ?? null : null;
  });
  readonly lastRunLabel = computed(() => {
    const run = this.environment().lastRun;
    return run ? `${triggerLabel(run.trigger)} ${RUN_STATE_LABEL[run.state].toLowerCase()}` : null;
  });
  readonly lastRunAge = computed(() => {
    const run = this.environment().lastRun;
    return run ? ago(formatAge(run.at, this.now())) : null;
  });

  reviewApproval(): void {
    const approval = this.environment().awaitingApproval;
    if (approval?.deploymentId) this.openRun.emit(approval.deploymentId);
    else this.openTools.emit();
  }
}
