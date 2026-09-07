import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskIntegrationStatus } from '../../features/git';
import { PendingButtonDirective } from '../async-feedback';
import { NotificationService } from '../../services/notification.service';
import { TaskService } from '../../services/task.service';

/**
 * AGT-2202 — the accept-safety badge. Renders the honest, git-derived
 * {@link TaskIntegrationStatus} on an accepted card (5-human-review / 6-completed
 * / 7-archive) so "Accept != Merge" is impossible to miss:
 *   - green  "merged @sha"          — every attributed commit is provably in develop,
 *   - orange "teilweise integriert" — some attributed commits are in develop, some are not,
 *   - amber  "NICHT integriert"     — accepted work is still not in develop,
 *   - red    "Integration failed"   — the integration step reached a failed outcome,
 *   - grey   "kein Branch"          — nothing to integrate (read-only / no code).
 *
 * Membership is derived from the SAME attributed `commits[]` list the card's
 * commit widget renders. Branch presence comes from the acceptance resolver's
 * projected `deliveryRef`, so a remote delivery cannot render as "kein Branch".
 *
 * Follows the {@link ExecutionLocationBadgeComponent} pill pattern. Purely
 * presentational; hidden when the card carries no integration verdict.
 */
@Component({
  selector: 'app-integration-status-badge',
  standalone: true,
  imports: [TooltipDirective, PendingButtonDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './integration-status-badge.component.html',
  styleUrl: './integration-status-badge.component.scss',
})
export class IntegrationStatusBadgeComponent {
  readonly integration = input<TaskIntegrationStatus | null | undefined>(null);
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  readonly recoveryPending = signal(false);
  private readonly notifications = inject(NotificationService);
  private readonly tasks = inject(TaskService);

  /** The card renders the badge only when a verdict is present. */
  readonly visible = computed(() => !!this.integration());

  /** Coarse visual kind for colour theming. */
  readonly kind = computed<'integrated' | 'partial' | 'pending' | 'conflict' | 'no-branch'>(() => {
    switch (this.integration()?.status) {
      case 'integrated': return 'integrated';
      case 'partial': return 'partial';
      case 'pending': return 'pending';
      case 'conflict-skipped': return 'conflict';
      default: return 'no-branch';
    }
  });

  /** True for the states that mean "accepted, but the code is NOT (fully) in develop". */
  readonly acute = computed(() => {
    const s = this.integration()?.status;
    return s === 'partial' || s === 'pending' || s === 'conflict-skipped';
  });

  readonly recoveryAvailable = computed(() => {
    const value = this.integration();
    if (value?.status !== 'conflict-skipped') return false;
    // Legacy payloads did not carry a classification and represented only
    // merge conflicts. Preserve their recovery action while new payloads use
    // the explicit capability bit.
    return value.failure?.rebaseRecoveryAvailable ?? true;
  });

  readonly label = computed(() => {
    const value = this.integration();
    if (!value) return '';
    if (value.repositories?.length) {
      return value.repositories.map((repository) => this.repositoryLine(repository)).join(' · ');
    }
    switch (value.status) {
      case 'integrated': return value.sha ? `merged @${value.sha}` : 'merged';
      case 'partial': return 'teilweise integriert';
      case 'pending': return 'NICHT integriert';
      case 'conflict-skipped': return value.failure?.label ?? 'Integration failed';
      default: return 'kein Branch';
    }
  });

  readonly glyph = computed(() => {
    switch (this.kind()) {
      case 'integrated': return '✓'; // check
      case 'partial': return '◐';    // half-filled circle
      case 'conflict': return '⚠';   // warning
      case 'pending': return '○';    // hollow circle
      default: return '–';           // en dash
    }
  });

  readonly tooltip = computed(() => {
    const value = this.integration();
    if (!value) return '';
    const branch = value.integrationBranch || 'develop';
    const head = (() => {
      switch (value.status) {
        case 'integrated':
          return value.sha
            ? `Integrated into ${branch} (${value.sha})`
            : `Integrated into ${branch}`;
        case 'partial':
          return `Partially integrated into ${branch} — some attributed commits are NOT in ${branch}`;
        case 'pending':
          return `Accepted, but NOT integrated into ${branch}`;
        case 'conflict-skipped':
          return value.failure?.label
            ? `${value.failure.label}; the work is NOT integrated into ${branch}`
            : `Integration into ${branch} failed; the work is NOT integrated`;
        default:
          return 'No task branch or commit to integrate';
      }
    })();
    const repositoryDetails = value.repositories?.map((repository) =>
      `${repository.repository}: ${repository.detail}`) ?? [];
    return [...new Set([head, ...repositoryDetails, value.failure?.reason, value.detail].filter(Boolean))].join('\n');
  });

  private repositoryLine(repository: NonNullable<TaskIntegrationStatus['repositories']>[number]): string {
    const integrated = repository.commits.filter((commit) => commit.onIntegrationBranch).length;
    const branch = repository.integrationBranch || 'develop';
    const release = repository.releaseBranch || 'main';
    const targets = repository.onReleaseBranch && release !== branch
      ? `${branch} and ${release}`
      : branch;
    return `${repository.repository} ${integrated}/${repository.commits.length} ${targets}`;
  }

  readonly ariaLabel = computed(() => {
    const value = this.integration();
    if (!value) return '';
    const branch = value.integrationBranch || 'develop';
    switch (value.status) {
      case 'integrated': return `Integrated into ${branch}`;
      case 'partial': return `Partially integrated into ${branch}`;
      case 'pending': return `Not integrated into ${branch}`;
      case 'conflict-skipped': return `${value.failure?.label ?? 'Integration failed'}; not integrated into ${branch}`;
      default: return 'No branch to integrate';
    }
  });

  queueRecovery(event: Event): void {
    event.stopPropagation();
    const jobId = this.jobId();
    if (!jobId || this.recoveryPending()) return;

    this.recoveryPending.set(true);
    this.tasks.queueIntegrationRecovery(jobId, this.watchPath() ?? undefined).subscribe({
      next: (response) => {
        this.recoveryPending.set(false);
        this.notifications.success(
          `Integration recovery queued: rebase ${response.deliveryRef} onto ${response.integrationBranch}.`,
        );
        this.tasks.refresh(true);
      },
      error: () => {
        this.recoveryPending.set(false);
        this.notifications.error('Could not queue the integration recovery round.');
      },
    });
  }
}
