import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import type { HttpErrorResponse } from '@angular/common/http';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { isMergedIntegrationStatus } from '../../features/git';
import type { IntegrationVerificationState, TaskIntegrationStatus } from '../../features/git';
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
 * AGT-3002: a merged verdict also says whether a gate passed on the tree that
 * carries the delivery ("· verified" / "· unverified"). Containment without a
 * gate is the acute orange "unverified" kind, never the green "merged" one.
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
  readonly retryPending = signal(false);
  private readonly notifications = inject(NotificationService);
  private readonly tasks = inject(TaskService);

  /** The card renders the badge only when a verdict is present. */
  readonly visible = computed(() => !!this.integration());

  /**
   * AGT-3002 - the gate evidence of a merged verdict. Null for every other
   * status and for a legacy card with no integration record (unknown).
   */
  readonly verificationState = computed<IntegrationVerificationState | null>(() => {
    const value = this.integration();
    if (!value || !isMergedIntegrationStatus(value.status)) return null;
    return value.verification?.state ?? null;
  });

  readonly unverified = computed(() => this.verificationState() === 'integrated-unverified');

  /**
   * Coarse visual kind for colour theming. AGT-2849: a delivery that only a
   * local ref can see shares the amber "accepted, not integrated" treatment,
   * because that is exactly what it is; the label and tooltip say why.
   */
  readonly kind = computed<'integrated' | 'unverified' | 'partial' | 'pending' | 'conflict' | 'no-branch'>(() => {
    if (this.unverified()) return 'unverified';
    switch (this.integration()?.status) {
      case 'integrated': return 'integrated';
      case 'partial': return 'partial';
      case 'pending': return 'pending';
      case 'merged-locally': return 'pending';
      case 'conflict-skipped': return 'conflict';
      default: return 'no-branch';
    }
  });

  /** True for the states that mean "accepted, but the code is NOT (fully) in develop". */
  readonly acute = computed(() => {
    const s = this.integration()?.status;
    return this.unverified()
      || s === 'partial' || s === 'pending' || s === 'merged-locally' || s === 'conflict-skipped';
  });

  readonly recoveryAvailable = computed(() => {
    const value = this.integration();
    if (value?.status !== 'conflict-skipped') return false;
    // Legacy payloads did not carry a classification and represented only
    // merge conflicts. Preserve their recovery action while new payloads use
    // the explicit capability bit.
    return value.failure?.rebaseRecoveryAvailable ?? true;
  });

  /**
   * AGT-2824 - a gate environment failure is a broken gate host, not a verdict
   * on the delivery, so the card offers a cheap replay of the integration that
   * reuses the review that already passed. CAC-18 keeps this failure in
   * `pending` rather than `conflict-skipped`, so it is keyed off the code.
   */
  readonly retryAvailable = computed(() =>
    this.integration()?.failure?.code === 'gate-environment-failure');

  readonly label = computed(() => {
    const base = this.statusLabel();
    switch (this.verificationState()) {
      case 'integrated-verified': return `${base} · verified`;
      case 'integrated-unverified': return `${base} · unverified`;
      default: return base;
    }
  });

  private readonly statusLabel = computed(() => {
    const value = this.integration();
    if (!value) return '';
    if (value.repositories?.length) {
      return value.repositories.map((repository) => this.repositoryLine(repository)).join(' · ');
    }
    switch (value.status) {
      case 'integrated': return value.sha ? `merged @${value.sha}` : 'merged';
      case 'not-applicable': return 'No integration needed';
      case 'merged-locally': return 'merged locally, not pushed';
      case 'partial': return 'teilweise integriert';
      case 'pending': return 'NICHT integriert';
      case 'conflict-skipped': {
        const base = value.failure?.label ?? 'Integration failed';
        const requeueLabel = this.requeueClassLabel();
        return requeueLabel ? `${base} · ${requeueLabel}` : base;
      }
      default: return 'kein Branch';
    }
  });

  /**
   * AGT-2749: display name for a failure the shared taxonomy attributes to
   * the host or the provider account. Null for `product`/`unknown` (and for
   * legacy records without a class) — those keep the plain failure label,
   * with no implication that a retry is in flight.
   */
  readonly requeueClassLabel = computed<'Infrastructure' | 'Quota' | null>(() => {
    switch (this.integration()?.failure?.failureClass) {
      case 'infrastructure': return 'Infrastructure';
      case 'quota': return 'Quota';
      default: return null;
    }
  });

  readonly glyph = computed(() => {
    switch (this.kind()) {
      case 'integrated': return '✓'; // check
      case 'unverified': return '!';
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
        case 'not-applicable':
          return 'This delivery has no repository change to integrate';
        case 'merged-locally':
          return `Merged into ${branch} locally, but not reachable from origin/${branch} yet`;
        case 'partial':
          return `Partially integrated into ${branch} — some attributed commits are NOT in ${branch}`;
        case 'pending':
          return `Accepted, but NOT integrated into ${branch}`;
        case 'conflict-skipped': {
          const requeueLabel = this.requeueClassLabel();
          const failureLabel = value.failure?.label
            ? `${value.failure.label}; the work is NOT integrated into ${branch}`
            : `Integration into ${branch} failed; the work is NOT integrated`;
          return requeueLabel
            ? `${failureLabel} — ${requeueLabel} fault, will be retried automatically`
            : failureLabel;
        }
        default:
          return 'No task branch or commit to integrate';
      }
    })();
    const repositoryDetails = value.repositories?.map((repository) =>
      `${repository.repository}: ${repository.detail}`) ?? [];
    return [...new Set([head, this.verificationLine(), ...repositoryDetails, value.failure?.reason, value.detail]
      .filter(Boolean))].join('\n');
  });

  /** AGT-3002 - the tooltip line naming the gate evidence of a merged verdict. */
  private verificationLine(): string | null {
    const verification = this.integration()?.verification;
    const state = this.verificationState();
    if (!verification || !state) return null;
    const tree = verification.sha ? ` ${verification.sha.slice(0, 7)}` : '';
    const reason = verification.reason ? ` ${verification.reason}` : '';
    return state === 'integrated-verified'
      ? `Integrated-verified: a gate passed on the merged tree${tree}.${reason}`
      : `Integrated-unverified: no gate passed on the merged tree${tree}.${reason}`;
  }

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
    const base = this.statusAriaLabel();
    switch (this.verificationState()) {
      case 'integrated-verified': return `${base}, verified by a gate`;
      case 'integrated-unverified': return `${base}, not verified by any gate`;
      default: return base;
    }
  });

  private readonly statusAriaLabel = computed(() => {
    const value = this.integration();
    if (!value) return '';
    const branch = value.integrationBranch || 'develop';
    switch (value.status) {
      case 'integrated': return `Integrated into ${branch}`;
      case 'not-applicable': return 'No integration needed';
      case 'merged-locally': return `Merged into ${branch} locally but not pushed to origin/${branch}`;
      case 'partial': return `Partially integrated into ${branch}`;
      case 'pending': return `Not integrated into ${branch}`;
      case 'conflict-skipped': return `${value.failure?.label ?? 'Integration failed'}; not integrated into ${branch}`;
      default: return 'No branch to integrate';
    }
  });

  retryIntegration(event: Event): void {
    event.stopPropagation();
    const jobId = this.jobId();
    if (!jobId || this.retryPending()) return;

    this.retryPending.set(true);
    this.tasks.retryIntegration(jobId, this.watchPath() ?? undefined).subscribe({
      next: (response) => {
        this.retryPending.set(false);
        if (response.status === 'integrated') {
          this.notifications.success(
            `Integration retried without a new review: merged into ${response.integrationBranch}.`,
          );
        } else {
          this.notifications.error(
            `The integration retry failed again: ${response.reason}`,
          );
        }
        this.tasks.refresh(true);
      },
      // A refused retry is a policy verdict, not a transport fault: the same
      // GateEnvironmentRetryPolicy that drives the automatic ladder answers 409
      // with the rule that refused it, so the operator reads that instead of a
      // generic failure.
      error: (failure: HttpErrorResponse) => {
        this.retryPending.set(false);
        const refusal = typeof failure.error?.error === 'string' ? failure.error.error : null;
        this.notifications.error(refusal ?? 'Could not retry the integration.');
      },
    });
  }

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
