import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TaskInfo, TaskReferenceLink, WaitsOnItem } from '../../../../models/task.model';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';
import { TaskSelectionService } from '../../state/task-selection.service';
import {
  canOfferRelease,
  dependentKey,
  releasableWaitsOnTargets,
  releaseGatedDependents,
} from './release-gate.model';

/**
 * AGT-2709 — the operator affordance for the release gate, rendered inside the
 * detail-view References section so both ends of a `releaseGate` edge are
 * actionable from wherever the operator happens to be looking:
 *
 * - **Target side**: a terminal task with release-gated dependents shows its
 *   current flag, the dependents the decision moves, and one button to grant or
 *   withdraw the release. Nothing in the lifecycle sets this flag, so without
 *   the button the only path was a hand-written `PUT /api/tasks/{id}/release`.
 * - **Dependent side**: a task blocked purely on someone else's missing release
 *   releases that target inline, so the "waits for release: KEY" state does not
 *   force a navigation to a (frequently archived) card first.
 *
 * The write is the plain endpoint call, not optimistic: releasing changes the
 * server-computed `waitsOn` overlay of every dependent, so `changed` asks the
 * parent to re-fetch rather than guessing the new projection locally.
 */
@Component({
  selector: 'app-release-gate-section',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './release-gate-section.component.html',
  styleUrl: './release-gate-section.component.scss',
})
export class ReleaseGateSectionComponent {
  private readonly tasks = inject(TaskService);
  private readonly selection = inject(TaskSelectionService);
  private readonly notifications = inject(NotificationService);

  readonly info = input.required<TaskInfo>();
  /** Incoming `dependsOn` links for this task, as loaded by the references section. */
  readonly dependents = input<readonly TaskReferenceLink[]>([]);

  /** Emitted after a successful write so the parent re-fetches the detail. */
  readonly changed = output<void>();

  /** Folder id of the task whose release write is in flight; null when idle. */
  readonly busyJobId = signal<string | null>(null);

  readonly released = computed(() => this.info().released === true);
  readonly gatedDependents = computed(() => releaseGatedDependents(this.dependents()));
  readonly canRelease = computed(() => canOfferRelease(this.info(), this.dependents()));
  readonly waitingTargets = computed(() => releasableWaitsOnTargets(this.info()));
  readonly visible = computed(() => this.canRelease() || this.waitingTargets().length > 0);

  readonly dependentKey = dependentKey;

  releaseStateLabel(): string {
    return this.released() ? 'Released' : 'Release pending';
  }

  releaseButtonLabel(): string {
    return this.released() ? 'Withdraw release' : 'Release for dependents';
  }

  releaseTooltip(): string {
    const keys = this.gatedDependents().map(dependentKey);
    return this.released()
      ? `Withdrawing the release blocks ${keys.join(', ')} again until it is granted anew.`
      : `Releasing unblocks ${keys.join(', ')}. Completion alone never sets this flag.`;
  }

  /** Grant or withdraw this task's own release flag. */
  toggleSelfRelease(): void {
    const info = this.info();
    const next = !this.released();
    const keys = this.gatedDependents().map(dependentKey);
    this.write(info.id, info.watchPath, next, next
      ? `${this.selfLabel()} released. ${keys.join(', ')} ${keys.length === 1 ? 'is' : 'are'} no longer waiting for release.`
      : `${this.selfLabel()} release withdrawn.`);
  }

  /** Release a waits-on target from this (dependent) task, without navigating. */
  releaseTarget(item: WaitsOnItem): void {
    if (!item.targetJobId) return;
    this.write(
      item.targetJobId,
      item.targetWatchPath ?? this.info().watchPath,
      true,
      `${item.key} released. ${this.selfLabel()} is no longer waiting for release.`,
    );
  }

  /** Operator-facing name of the current task: its stable key, never the composite taskKey. */
  private selfLabel(): string {
    const info = this.info();
    return info.displayKey || info.key || info.id;
  }

  /** Open the waits-on target so the operator can inspect it before releasing. */
  openTarget(item: WaitsOnItem): void {
    const target = this.tasks
      .jobs()
      .find(task => task.id === item.targetJobId && task.watchPath === item.targetWatchPath);
    if (!target) {
      this.notifications.info(`${item.key} is not loaded in the current workspace view.`);
      return;
    }
    this.selection.openDetail(target);
  }

  targetLabel(item: WaitsOnItem): string {
    return item.targetTitle ? `${item.key} — ${item.targetTitle}` : item.key;
  }

  private write(jobId: string, watchPath: string, released: boolean, success: string): void {
    if (this.busyJobId() !== null) return;
    this.busyJobId.set(jobId);
    this.tasks.setTaskReleased(jobId, released, watchPath).subscribe({
      next: () => {
        this.busyJobId.set(null);
        this.notifications.success(success, 'Release gate');
        this.changed.emit();
      },
      error: () => {
        this.busyJobId.set(null);
        this.notifications.error(
          'The release flag could not be written. The task may have been moved or removed.',
          'Release gate',
        );
      },
    });
  }
}
