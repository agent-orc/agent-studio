import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import type { PlanningSpawnSummary, TaskInfo } from '../../../../models/task.model';
import {
  TaskReferenceMicrocardComponent,
  type TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';

interface ReferenceStatusResponse {
  items: TaskReferenceStatus[];
}

/**
 * AGT-2069 — spawn-visibility + spawn-contract panel for a planning task's
 * detail. Answers "total krass" whether this planning run produced follow-up
 * cards:
 * <ul>
 *   <li>follow-ups exist -> renders each as an AGT-2050 reference microcard
 *       ("spawnt: AGT-xxxx");</li>
 *   <li>none, not declared -> a compact warning with promote and declaration
 *       actions;</li>
 *   <li>declared -> shows the deliberate no-follow-up declaration + an undo.</li>
 * </ul>
 * The unresolved status mirrors the accept-dialog guard for the AGT-1915 trap.
 * Self-contained: it fetches the spawned cards' reference status itself and
 * writes the declaration through the planning-closure endpoint, holding a local
 * summary override so the panel updates instantly; it also emits {@link changed}
 * so the parent re-fetches the detail (keeping the header accept-gate in sync).
 */
@Component({
  selector: 'app-planning-spawn-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, TaskReferenceMicrocardComponent, StatusIndicatorComponent],
  templateUrl: './planning-spawn-panel.component.html',
  styleUrl: './planning-spawn-panel.component.scss',
})
export class PlanningSpawnPanelComponent {
  readonly job = input.required<TaskInfo>();
  /** Presentation-only bridge to the Overview-owned promote flow. */
  readonly promoteAvailable = input(false);
  readonly promotePending = input(false);
  readonly promoteRequested = output<void>();
  /** Emitted after a declaration write so the parent re-fetches the detail. */
  readonly changed = output<void>();

  private readonly http = inject(HttpClient);
  private readonly jobService = inject(TaskService);
  private readonly notifs = inject(NotificationService);

  /** Local override so a declare/undo reflects instantly, ahead of the re-fetch. */
  private readonly summaryOverride = signal<PlanningSpawnSummary | null>(null);

  readonly summary = computed<PlanningSpawnSummary | null>(
    () => this.summaryOverride() ?? this.job().planningSpawn ?? null,
  );

  /** Show only for planning tasks that carry the backend projection. */
  readonly show = computed<boolean>(() => this.job().mode === 'planning' && !!this.summary());

  readonly spawned = computed(() => this.summary()?.spawned ?? []);
  readonly hasSpawns = computed<boolean>(() => this.spawned().length > 0);
  readonly noFollowUpDeclared = computed<boolean>(() => this.summary()?.noFollowUpDeclared ?? false);
  readonly noFollowUpReason = computed<string | null>(() => this.summary()?.noFollowUpReason ?? null);
  /** True when this planning task risks the AGT-1915 trap: nothing spawned, nothing declared. */
  readonly atRisk = computed<boolean>(() => !this.hasSpawns() && !this.noFollowUpDeclared());

  /** Resolved reference status per spawned key (for the microcards). */
  readonly statuses = signal<ReadonlyMap<string, TaskReferenceStatus>>(new Map());

  readonly declaring = signal(false);
  readonly reasonDraft = signal('');
  readonly busy = signal(false);

  /** Reset the local override when the parent hands a different task (navigation). */
  private readonly resetOnJobChange = effect(() => {
    void this.job().id;
    this.summaryOverride.set(null);
    this.declaring.set(false);
  });

  /** Hydrate each spawned key into an AGT-2050 reference status for the microcards. */
  private readonly loadStatuses = effect(() => {
    const keys = this.spawned()
      .map((s) => s.targetKey)
      .filter((k): k is string => !!k);
    if (keys.length === 0) {
      this.statuses.set(new Map());
      return;
    }
    this.http.post<ReferenceStatusResponse>('/api/tasks/reference-status', { keys }).subscribe({
      next: (res) => {
        const map = new Map<string, TaskReferenceStatus>();
        for (const item of res.items ?? []) map.set(item.key.toUpperCase(), item);
        this.statuses.set(map);
      },
      error: () => {
        /* keep prior map; chips fall back to the key text */
      },
    });
  });

  statusFor(key: string | null | undefined): TaskReferenceStatus | null {
    if (!key) return null;
    return this.statuses().get(key.toUpperCase()) ?? null;
  }

  openDeclare(): void {
    this.reasonDraft.set('');
    this.declaring.set(true);
  }

  cancelDeclare(): void {
    this.declaring.set(false);
  }

  submitDeclare(): void {
    if (this.busy()) return;
    const job = this.job();
    this.busy.set(true);
    this.jobService
      .setPlanningClosure(job.id, true, this.reasonDraft().trim() || null, job.watchPath)
      .subscribe({
        next: (summary) => {
          this.summaryOverride.set(summary);
          this.declaring.set(false);
          this.busy.set(false);
          this.changed.emit();
          this.notifs.success(
            'Recorded: no follow-up cards are intended for this planning task.',
            'Declaration saved',
          );
        },
        error: () => {
          this.busy.set(false);
          this.notifs.warning(
            'Could not save the no-follow-up declaration. Try again in a moment.',
            'Declaration failed',
          );
        },
      });
  }

  clearDeclaration(): void {
    if (this.busy()) return;
    const job = this.job();
    this.busy.set(true);
    this.jobService.setPlanningClosure(job.id, false, null, job.watchPath).subscribe({
      next: (summary) => {
        this.summaryOverride.set(summary);
        this.busy.set(false);
        this.changed.emit();
      },
      error: () => {
        this.busy.set(false);
        this.notifs.warning('Could not clear the declaration.', 'Update failed');
      },
    });
  }
}
