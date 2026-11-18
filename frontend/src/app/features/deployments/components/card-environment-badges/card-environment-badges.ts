import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { CardEnvironmentBadge } from '../../models/card-deployment.model';
import { CardDeploymentService } from '../../services/card-deployment.service';

/** Current deployment truth per environment (R4 card contract); unknown states stay neutral. */
const CARD_ENVIRONMENT_TONE: Readonly<Record<CardEnvironmentBadge['state'], StatusTone>> = {
  succeeded: 'success',
  failed: 'danger',
  'failed-rolled-back': 'danger',
  'rolled-back': 'warning',
  'awaiting-approval': 'warning',
  running: 'running',
  queued: 'neutral',
  cancelled: 'neutral',
  'not-deployed': 'neutral',
};

export function cardEnvironmentTone(state: string): StatusTone {
  return Object.hasOwn(CARD_ENVIRONMENT_TONE, state) ? CARD_ENVIRONMENT_TONE[state as CardEnvironmentBadge['state']] : 'neutral';
}

@Component({
  selector: 'app-card-environment-badges',
  standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './card-environment-badges.html',
  styleUrl: './card-environment-badges.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CardEnvironmentBadgesComponent {
  private readonly deploy = inject(CardDeploymentService);
  readonly task = input.required<TaskInfo>();
  readonly badges = computed(() => this.deploy.badges(this.task()));

  constructor() { this.deploy.observe(); }

  readonly tone = cardEnvironmentTone;

  label(name: string, kind: string, state: string): string {
    if (state === 'not-deployed') return kind === 'production' ? 'Not yet in Production' : 'Staging pending';
    const wording: Record<string, string> = {
      succeeded: 'Live', failed: 'Failed', 'failed-rolled-back': 'Failed, rolled back',
      'rolled-back': 'Rolled back', 'awaiting-approval': 'Approval pending',
      running: 'Deploying', queued: 'Queued', cancelled: 'Cancelled',
    };
    return `${name} · ${wording[state] ?? 'Status unavailable'}`;
  }

  text(badge: CardEnvironmentBadge): string {
    return [this.label(badge.environmentName, badge.kind, badge.state), badge.releaseVersion, badge.atUtc?.slice(0, 10)]
      .filter(Boolean).join(' · ');
  }

  detail(badge: CardEnvironmentBadge): string | null {
    return badge.releaseVersion ? 'Release ' + badge.releaseVersion + (badge.atUtc ? ' · ' + badge.atUtc : '') : null;
  }
}
