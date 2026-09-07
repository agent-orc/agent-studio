import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';

/**
 * The task the release decision is about. Both directions of the gate render
 * the same control, so the dependent card can release its target inline
 * instead of navigating to it: the waits-on overlay already carries the
 * target's job id and watch path.
 */
export interface ReleaseGateTarget {
  /** Folder id of the task that carries the `released` flag. */
  jobId: string;
  /** Watch path of the target; the target may live in another project. */
  watchPath?: string;
  /** Stable key, used for the operator-facing copy. */
  key: string;
  /** Current value of the explicit release flag. */
  released: boolean;
}

/**
 * AGT-2709 operator affordance for the release gate. A `dependsOn` edge with
 * `releaseGate: true` is only fulfilled once its target task is terminal AND
 * carries the explicit `released` flag; completion never sets that flag. Until
 * this control existed the flag could only be set through
 * `PUT /api/tasks/{id}/release`, so an operator looking for "where do I release
 * this?" found nothing in the UI.
 *
 * The control is deliberately reversible: a release can be withdrawn, which
 * puts the gated dependents back into "waits for release". Both directions
 * write one `task_released` timeline row naming the actor, so the decision
 * stays auditable.
 *
 * Visibility is the caller's decision (the target must be terminal and
 * actually gated); this component owns the write, the pending state, and the
 * copy naming the dependents the decision affects.
 */
@Component({
  selector: 'app-release-gate-action',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PendingButtonDirective, AppTooltipDirective],
  templateUrl: './release-gate-action.component.html',
  styleUrl: './release-gate-action.component.scss',
})
export class ReleaseGateActionComponent {
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);

  readonly target = input.required<ReleaseGateTarget>();

  /** Keys of the release-gated dependents this decision unblocks or re-blocks. */
  readonly dependents = input<readonly string[]>([]);

  /** Emits the new flag value after the write landed, so the host can re-fetch. */
  readonly releasedChange = output<boolean>();

  readonly busy = signal(false);

  readonly released = computed(() => this.target().released === true);

  readonly stateLabel = computed(() => (this.released() ? 'Released' : 'Release pending'));

  readonly actionLabel = computed(() =>
    this.released() ? 'Withdraw release' : 'Release for dependents');

  /** "Unblocks APP-1 +2" - the full list stays in the tooltip. */
  readonly dependentsLabel = computed(() => {
    const keys = this.dependents();
    if (keys.length === 0) return '';
    const extra = keys.length - 1;
    const verb = this.released() ? 'Released for' : 'Unblocks';
    return `${verb} ${keys[0]}${extra > 0 ? ` +${extra}` : ''}`;
  });

  readonly tooltip = computed(() => {
    const keys = this.dependents();
    const head = this.released()
      ? `${this.target().key} is released. Withdrawing it makes the gated dependents wait again.`
      : `${this.target().key} is complete but not released, so release-gated dependents stay blocked.`;
    return keys.length === 0 ? head : `${head}\n${keys.join('\n')}`;
  });

  toggle(): void {
    if (this.busy()) return;
    const target = this.target();
    const next = !this.released();
    this.busy.set(true);
    this.tasks.setTaskReleased(target.jobId, next, target.watchPath).subscribe({
      next: () => {
        this.busy.set(false);
        this.notifications.success(
          next
            ? `${target.key} released${this.dependentsSuffix()}.`
            : `Release for ${target.key} withdrawn.`,
        );
        this.releasedChange.emit(next);
      },
      error: () => {
        this.busy.set(false);
        this.notifications.error(
          `The release flag on ${target.key} could not be written.`,
          'Release failed',
        );
      },
    });
  }

  private dependentsSuffix(): string {
    const keys = this.dependents();
    return keys.length === 0 ? '' : `, unblocking ${keys.join(', ')}`;
  }
}
