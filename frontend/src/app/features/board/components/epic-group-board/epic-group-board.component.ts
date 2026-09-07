import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { buildEpicGroups, EpicGroupView } from '../epic-grouping.util';
import { TaskCardComponent } from '../task-card/task-card.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { StudioIconComponent, StudioIconName } from '../../../../components/studio-icon/studio-icon.component';
import { laneName } from '../../../../models/lane-presentation';

/**
 * Group-by-epic board view: the "Gruppieren nach Epic" toggle swaps the lane
 * columns for this tree. Each epic is a section (the epic card plus its
 * sub-tasks) with a live "completed / total" rollup that mirrors the backend
 * `GET /api/epics`. Ordinary tasks with no epic and orphaned sub-tasks get
 * their own synthetic sections via `buildEpicGroups`.
 *
 * Read-only and additive: it keeps the epic itself as a normal card so the
 * EPIC badge and card-level actions stay available, then renders sub-tasks as
 * compact text rows. Clicking the epic card or a sub-task row opens detail,
 * same as the lane view.
 */
@Component({
  selector: 'app-epic-group-board',
  standalone: true,
  imports: [TaskCardComponent, TooltipDirective, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './epic-group-board.component.html',
  styleUrl: './epic-group-board.component.scss',
})
export class EpicGroupBoardComponent {
  readonly tasks = input<readonly TaskInfo[]>([]);
  readonly compact = input<boolean>(false);
  readonly highlightJobId = input<string | null>(null);
  readonly jobClick = output<TaskInfo>();

  readonly groups = computed<EpicGroupView[]>(() => buildEpicGroups(this.tasks()));

  /** Ids the operator collapsed in this local view. New epic sections start open. */
  private readonly collapsed = signal<ReadonlySet<string>>(new Set());

  isCollapsed(id: string): boolean {
    return this.collapsed().has(id);
  }

  toggleCollapse(id: string): void {
    const next = new Set(this.collapsed());
    if (next.has(id)) next.delete(id);
    else next.add(id);
    this.collapsed.set(next);
  }

  /** Header glyph: epic puzzle piece, a folder for "No epic", a warning for orphans. */
  groupIcon(group: EpicGroupView): StudioIconName {
    if (group.epic) return 'epic';
    return group.id === '__orphan__' ? 'warn' : 'folder';
  }

  laneLabel(state: string): string {
    return laneName(state);
  }

  progressTooltip(group: EpicGroupView): string {
    return `${group.completed} of ${group.total} sub-tasks done (${group.inProgress} in progress, ${group.open} open)`;
  }

  verdictLabel(verdict: TaskInfo['orchestratorVerdict']): string | null {
    return verdict ? verdict.replace(/-/g, ' ') : null;
  }
}
