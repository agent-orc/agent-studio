import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  OnInit,
  output,
  signal,
} from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { TaskService } from '../../../../services/task.service';
import { projectIdentity } from '../../../../services/project-identity.util';
import type { EpicRollup } from '../../../../models/task.model';
import { EpicCreateDialogComponent } from '../epic-create-dialog/epic-create-dialog.component';
import { laneName } from '../../../../models/lane-presentation';

/** Project the overview is scoped to; null means the cross-project view. */
export interface EpicOverviewScope {
  name: string;
  watchPath: string;
}

/**
 * Dedicated read-only epic overview at `#/epics`. Lists every epic from
 * `GET /api/epics` with a segmented done / in-progress / open progress bar
 * and an expandable sub-task list. The board already offers assignment
 * (create dialog way 1, card context menu way 2); this screen is purely the
 * "where do my epics stand" surface, so the only mutation it surfaces is
 * navigation: clicking an epic or a sub-task opens that card's detail.
 *
 * The host owns the route
 * (`EpicOverviewService`) and the detail-open flow; this component fetches,
 * renders, and emits navigation requests.
 */
@Component({
  selector: 'app-epic-overview-screen',
  standalone: true,
  imports: [NgTemplateOutlet, TooltipDirective, EpicCreateDialogComponent, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './epic-overview-screen.component.html',
  styleUrl: './epic-overview-screen.component.scss',
})
export class EpicOverviewScreenComponent implements OnInit {
  private readonly jobs = inject(TaskService);

  /**
   * When set, the screen narrows to a single project: the list shows only
   * that project's epics and the "create epic" affordances light up (the
   * dialog needs a watch path to target). null keeps the cross-project view
   * read-only, since there is no single project to create into.
   */
  readonly scopedProject = input<EpicOverviewScope | null>(null);
  readonly mutationsBlocked = input(false);

  /** Bubbles a click on an epic or sub-task so the host opens its detail. */
  readonly openTask = output<{ jobId: string; watchPath: string }>();

  readonly epics = signal<EpicRollup[]>([]);
  readonly completedEpics = signal<EpicRollup[]>([]);
  readonly completedCount = signal(0);
  readonly completedOpen = signal(false);
  readonly completedLoaded = signal(false);
  readonly completedLoading = signal(false);
  readonly loading = signal(false);
  readonly error = signal(false);
  /** Epic ids whose sub-task list is currently expanded. */
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  /** Whether the create-epic dialog is mounted. */
  readonly showCreate = signal(false);

  /** Epics shown after the optional project scope is applied. */
  readonly visibleEpics = computed(() => {
    const scope = this.scopedProject();
    const all = this.epics();
    const scoped = scope ? all.filter((e) => e.projectName === scope.name) : all;
    // Archived zero-member epics are deliberate cleanup records, not useful
    // history. Completed epics remain visible because their member rollup is
    // the history operators need to inspect.
    return scoped.filter((e) => e.subTaskTotal > 0 || e.state !== '7-archive');
  });

  readonly activeEpics = computed(() =>
    this.visibleEpics().filter((e) => e.subTaskTotal === 0 || e.completed < e.subTaskTotal),
  );
  readonly totalEpics = computed(() => this.activeEpics().length + this.completedCount());
  /** Create is only offered when a single project is in scope. */
  readonly canCreate = computed(() => this.scopedProject() !== null && !this.mutationsBlocked());

  ngOnInit(): void {
    this.loadEpics();
    this.loadCompletedCount();
  }

  private loadEpics(): void {
    this.loading.set(true);
    this.error.set(false);
    this.jobs.getEpics(false, 'active', this.scopedProject()?.name).subscribe({
      next: (list) => {
        this.epics.set(list ?? []);
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  private loadCompletedCount(): void {
    this.jobs.getCompletedEpicCount(false, this.scopedProject()?.name).subscribe({
      next: ({ count }) => this.completedCount.set(count),
      error: () => this.completedCount.set(0),
    });
  }

  toggleCompleted(): void {
    const open = !this.completedOpen();
    this.completedOpen.set(open);
    if (!open || this.completedLoaded() || this.completedLoading()) return;
    this.completedLoading.set(true);
    this.jobs.getEpics(false, 'completed', this.scopedProject()?.name).subscribe({
      next: (list) => {
        this.completedEpics.set(list ?? []);
        this.completedCount.set(list?.length ?? 0);
        this.completedLoaded.set(true);
        this.completedLoading.set(false);
      },
      error: () => this.completedLoading.set(false),
    });
  }

  openCreate(): void {
    if (!this.canCreate()) return;
    this.showCreate.set(true);
  }

  closeCreate(): void {
    this.showCreate.set(false);
  }

  onEpicCreated(): void {
    this.showCreate.set(false);
    this.loadEpics();
    this.loadCompletedCount();
  }

  isExpanded(epicId: string): boolean {
    return this.expanded().has(epicId);
  }

  toggleExpanded(epicId: string): void {
    const next = new Set(this.expanded());
    if (next.has(epicId)) next.delete(epicId); else next.add(epicId);
    this.expanded.set(next);
  }

  /** Width % of a segment within the progress bar; 0 total renders empty. */
  segmentPct(count: number, total: number): number {
    if (total <= 0) return 0;
    return (count / total) * 100;
  }

  identityFor(name: string) {
    return projectIdentity(name);
  }

  /** Lane display name from the one catalogue (AGT-2715). */
  laneLabel(state: string): string {
    return laneName(state);
  }

  verdictLabel(verdict: string | null | undefined): string | null {
    if (!verdict) return null;
    return verdict.replace(/-/g, ' ');
  }

  completedDate(value: string | null | undefined): string | null {
    if (!value) return null;
    return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(new Date(value));
  }

  openEpic(epic: EpicRollup): void {
    this.openTask.emit({ jobId: epic.id, watchPath: epic.watchPath });
  }

  openSubTask(epic: EpicRollup, subId: string): void {
    this.openTask.emit({ jobId: subId, watchPath: epic.watchPath });
  }

  trackByEpic = (_: number, epic: EpicRollup) => epic.id;
}
