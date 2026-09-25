import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  output,
  signal,
} from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { MenuComponent, MenuItem, MenuItemClickEvent } from '../../../../components/menu';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { ExecutionLocationBadgeComponent }
  from '../../../../components/execution-location-badge/execution-location-badge.component';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { laneTone as laneToneFor } from '../../../../models/lane-presentation';
import { stateLabel as fmtStateLabel } from '../../../../services/format.util';
import type { RunActivityBadge } from '../../../../services/run-activity.util';
import type { TaskDetail } from '../../../../models/task.model';

/** Which of the task detail panes the shell currently shows. */
export interface ShellPanesVisible {
  prompt: boolean;
  protocol: boolean;
  git: boolean;
}

/** Where the lane pager stands for the open task. */
export interface StudioTabPager {
  position: number;
  total: number;
  laneState: string;
}

/** One navigable lane in the slim header's lane dropdown. */
export interface StudioLaneOption {
  state: string;
  label: string;
}

/**
 * The triage cluster's rendered state. The decisions behind it (which action is
 * primary, whether git provenance has settled) stay with the shell, which owns
 * the task detail instance they are read from.
 */
export interface StudioTabTriage {
  hasActions: boolean;
  primaryId: string | null;
  primaryLabel: string;
  primaryTooltip: string;
  awaitingGit: boolean;
  blockedByIntegration: boolean;
  actingId: string | null;
  menuItems: MenuItem[];
}

/**
 * The studio tab bar's action strip: document history, board actions, and the
 * open task's run badge, pane toggles, lane pager, and triage cluster.
 *
 * AGT-2819: this was 168 lines of markup inlined in `app.html`, which made the
 * shell's template the single largest template in the application and left the
 * strip unreachable by any test that did not mount the whole app. It is one
 * region of the UI with one job, so it is one component here.
 *
 * It stays presentational on purpose. The shell keeps deciding *what* the
 * actions are (they are derived from the embedded task detail instance it
 * owns); this component decides only how they render and reports what the user
 * pressed. The one piece of state it owns outright is its own overflow menu,
 * because nothing outside the strip can observe it.
 */
@Component({
  selector: 'app-studio-tab-actions',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    TooltipDirective,
    MenuComponent,
    StudioIconComponent,
    ExecutionLocationBadgeComponent,
    PendingButtonDirective,
  ],
  templateUrl: './studio-tab-actions.component.html',
  styleUrl: './studio-tab-actions.component.scss',
})
export class StudioTabActionsComponent {
  /** Renders the back/forward pair for tabs that keep a document history. */
  readonly documentHistoryVisible = input.required<boolean>();
  readonly canNavigateBack = input.required<boolean>();
  readonly canNavigateForward = input.required<boolean>();
  readonly documentHistoryLength = input.required<number>();

  /** Board tab actions. */
  readonly boardActionsVisible = input.required<boolean>();
  readonly groupByEpic = input.required<boolean>();

  /** Blocks every mutating action while an update is in flight. */
  readonly mutationsBlocked = input.required<boolean>();

  /**
   * The open task, or null when the active tab is not a settled task tab. The
   * whole task cluster renders off this one input, so the strip can never show
   * half a task's actions.
   */
  readonly task = input.required<TaskDetail | null>();
  readonly runActivity = input.required<RunActivityBadge | null>();
  readonly panes = input.required<ShellPanesVisible>();
  readonly pager = input.required<StudioTabPager>();
  readonly laneOptions = input.required<readonly StudioLaneOption[]>();
  readonly triage = input.required<StudioTabTriage>();

  readonly navigateDocumentHistory = output<1 | -1>();
  readonly toggleGroupByEpic = output<void>();
  readonly addTask = output<void>();
  readonly togglePane = output<'prompt' | 'protocol' | 'git'>();
  readonly previousTask = output<void>();
  readonly nextTask = output<void>();
  readonly laneChange = output<Event>();
  readonly triagePrimary = output<void>();
  readonly triageMenuItem = output<MenuItemClickEvent>();

  readonly overflowOpen = signal(false);
  readonly overflowAnchor = signal<HTMLElement | null>(null);

  /** Commit count on the git pane toggle's badge; hidden at zero. */
  readonly gitCommitCount = computed(() => {
    const info = this.task()?.info;
    if (!info) return 0;
    return info.commits?.length || (info.commit ? 1 : 0);
  });

  /** True when the execution-location badge has something to say. */
  readonly executionLocation = computed(() => {
    const execution = this.task()?.info.executionLocation;
    if (!execution || execution.state === 'no-active-execution') return null;
    return execution;
  });

  /** Lane tone key for the slim header's lane chip (AGT-2715). */
  laneTone(state: string): string {
    return laneToneFor(state);
  }

  /** A lane the dropdown already lists; anything else gets an extra option. */
  isStandardLane(state: string): boolean {
    return this.laneOptions().some(option => option.state === state);
  }

  stateLabel(state: string): string {
    return fmtStateLabel(state);
  }

  onToggleOverflow(event: MouseEvent): void {
    event.stopPropagation();
    if (this.mutationsBlocked()) return;
    this.overflowAnchor.set(event.currentTarget as HTMLElement);
    this.overflowOpen.update(open => !open);
  }

  closeOverflow(): void {
    this.overflowOpen.set(false);
  }

  onOverflowItemClick(event: MenuItemClickEvent): void {
    this.overflowOpen.set(false);
    this.triageMenuItem.emit(event);
  }
}
