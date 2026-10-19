import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { DatePipe } from '@angular/common';
import {
  OUTCOME_LABEL,
  formatAge,
  shortArtifact,
  type EnvironmentCardView,
} from '../../state/deploy-area.model';

function ago(age: string | null): string | null {
  return age && age !== 'just now' ? `${age} ago` : age;
}

const HEALTH_LABEL = { healthy: 'Healthy', unhealthy: 'Unhealthy', unknown: 'Unknown' } as const;

/** Status card of one environment: release, age, cards, health, and the last run. */
@Component({
  selector: 'app-deploy-environment-card',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './deploy-environment-card.html',
  styleUrl: './deploy-environment-card.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DeployEnvironmentCardComponent {
  readonly environment = input.required<EnvironmentCardView>();
  readonly now = input.required<number>();
  readonly openRun = output<string>();
  readonly openTools = output<void>();

  readonly headingId = computed(() => `deploy-environment-${this.environment().id}-title`);
  readonly release = computed(() => {
    const env = this.environment();
    if (!env.artifact) return null;
    return env.releaseVersion ? `Release ${env.releaseVersion}` : shortArtifact(env.artifact);
  });
  readonly age = computed(() => ago(formatAge(this.environment().deployedAt, this.now())));
  readonly cards = computed(() => {
    const cards = this.environment().cards;
    if (cards.status === 'unknown') return { known: false, text: cards.reason };
    return { known: true, text: cards.keys.length ? cards.keys.join(', ') : 'No cards in this release' };
  });
  readonly health = computed(() => {
    const health = this.environment().health;
    if (health.status === 'no-checks') return { tone: 'neutral', label: 'No checks' };
    return { tone: health.state, label: HEALTH_LABEL[health.state] };
  });
  readonly lastRunLabel = computed(() => {
    const run = this.environment().lastRun;
    if (!run) return null;
    const kind = run.kind === 'rollback' ? 'Rollback' : 'Deploy';
    return `${kind} ${OUTCOME_LABEL[run.outcome].toLowerCase()}`;
  });
  readonly lastRunAge = computed(() => {
    const run = this.environment().lastRun;
    return run ? ago(formatAge(run.finishedAt ?? run.queuedAt, this.now())) : null;
  });

  protected readonly shortArtifact = shortArtifact;
}
