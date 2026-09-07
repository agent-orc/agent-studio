import { ChangeDetectionStrategy, Component, ElementRef, ViewChild, computed, effect, inject, input, output, signal } from '@angular/core';
import { TaskInfo, TaskState } from '../../../../models/task.model';
import { laneShortName } from '../../../../models/lane-presentation';
import {
  formatDateTime as fmtDateTime,
  formatRelativeShort as fmtRelativeShort,
  stateLabel as fmtStateLabel,
  cliTypeLabel,
  taskModeIcon,
  taskModeLabel,
} from '../../../../services/format.util';
import { NowTickService } from '../../../../services/now-tick.service';
import { projectIdentity } from '../../../../services/project-identity.util';
import { ProjectHygieneBadgeComponent } from '../hygiene-strip/project-hygiene-badge/project-hygiene-badge.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { MenuComponent, MenuItem, MenuItemClickEvent } from '../../../../components/menu';
import { NotificationService } from '../../../../services/notification.service';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import {
  MergeAcceptView,
  TriageActionPayload,
  TriageButton,
  laneLabelFor,
  mergeAcceptViewFor,
  overflowActionsFor,
  primaryActionFor,
} from '../../state/triage-actions.model';
import type { LandedState } from '../../../git';
import { buildThinkingLevelIndicator } from '../../../../services/thinking-level.util';
import { ModelLevelIndicatorComponent } from '../../../../components/model-level-indicator/model-level-indicator.component';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { ExecutionLocationBadgeComponent } from '../../../../components/execution-location-badge/execution-location-badge.component';
import { CopyableTaskKeyComponent } from '../../../../components/copyable-task-key/copyable-task-key.component';
import { RemoteDispatchRejectionComponent } from '../../../../components/remote-dispatch-rejection/remote-dispatch-rejection.component';
/** Top header of the job-detail view: back button, editable title, state pill,
 * and the lane's primary triage action plus
 * an overflow menu of the remaining lane actions. The bottom-of-detail
 * triage bar that used to host these is gone (the operator reported the
 * "Human Review v" trigger row still rendering after the first attempt at folding it up). Title-edit state is owned by the parent and passed
 * via inputs/outputs.
 */
@Component({
  selector: 'app-detail-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ProjectHygieneBadgeComponent, TooltipDirective, MenuComponent, ModelLevelIndicatorComponent, PendingButtonDirective, ExecutionLocationBadgeComponent, CopyableTaskKeyComponent, RemoteDispatchRejectionComponent],
  templateUrl: './detail-header.component.html',
  styleUrl: './detail-header.component.scss'
})
export class DetailHeaderComponent {
  readonly info = input.required<TaskInfo>();
  readonly defaultThinkingLevel = input<string | null>(null);
  readonly headerModel = computed(() => {
    const info = this.info();
    return info.execution?.model ?? info.model ?? null;
  });
  readonly thinkingLevelIndicator = computed(() => buildThinkingLevelIndicator(
    this.info().execution, this.info().thinkingLevel, this.defaultThinkingLevel(), this.headerModel(),
  ));
  readonly headerModelTooltip = computed(() => [
    `Model ID: ${this.headerModel() ?? 'Not set'}`, `Thinking level: ${this.thinkingLevelIndicator()?.effective ?? 'CLI default'}`,
    `CLI: ${this.info().cliType ? cliTypeLabel(this.info().cliType!) : 'Not set'}`,
  ].join('\n'));
  readonly editingTitle = input(false);
  readonly titleDraft = input<string>('');
  readonly savingTitle = input(false);
  readonly movingToTop = input(false);
  /**
   * State of the lane the pager iterates (the snapshot's lane, falling back
   * to the open job's state). Drives the lane dropdown's selected value:
   * the dropdown is navigation-only, so it reflects "which lane Prev/Next
   * pages through", not necessarily the open job's live state (the two can
   * diverge after an external lane change).
   */
  readonly pagerLaneState = input<string>('');
  /** Lane pager position (1-based). 0 means no pager snapshot active. */
  readonly pagerPosition = input(0);
  /** Lane pager total. 0 hides the pager. */
  readonly pagerTotal = input(0);
  readonly pagerCanPrev = input(false);
  readonly pagerCanNext = input(false);
  /**
   * True while the selection is fetching the next/previous task without a
   * warmed prefetch. Renders a small spinner in the pager cluster so a
   * (non-instant) pager/cursor step shows the reload is in progress.
   */
  readonly loading = input(false);
  /** Human-readable label of the snapshot's original lane (e.g. "Ready"). */
  readonly pagerLaneLabel = input<string>('');
  /** Index of the open job inside the live lane peers (0-based; -1 == unknown). */
  readonly laneIndex = input(0);
  /** Total peers in the current lane (0 == no peers). */
  readonly laneSize = input(0);
  /** True while the update-service is mid-update; disables triage actions. */
  readonly mutationsBlocked = input(false);
  /** Stable id of the triage action currently in flight (null when idle). */
  readonly triageActingId = input<string | null>(null);
  /** Whether the task-level commit actions should be exposed in the overflow menu. */
  readonly commitActionsAvailable = input(false);
  /**
   * Live-derived landed position of the task's work (graph-derived provenance
   * view; null when unknown / still loading). Lets the Human Review acceptance
   * primary refine a canonical integrated result to "Released to main".
   * `info.integration` remains the only proof of target-branch membership.
   */
  readonly landedState = input<LandedState | null>(null);
  /**
   * True while the graph-derived git status (branch/merge position) for the open
   * job has not settled yet. Gates the git-dependent acceptance primary: until
   * the truth is known the button must not be clickable and must not show a
   * guessed label, because `landedState` is still `null` and would render the
   * "not yet merged" default that later flips to "already merged" (AGT-2006).
   */
  readonly gitInfoLoading = input(false);
  readonly commitMessageDraft = input<string>('');
  readonly generatingCommitMessage = input(false);
  readonly committing = input(false);

  readonly back = output<void>();
  readonly startTitleEdit = output<void>();
  readonly cancelTitleEdit = output<void>();
  readonly saveTitle = output<void>();
  readonly titleDraftChange = output<string>();
  /**
   * Delete request. The overflow menu's Delete row routes here instead of
   * through `triageAction` so the existing `boardMutations.deleteFromDetail`
   * confirm dialog + pager-aware advance stay in charge of destructive ops.
   */
  readonly deleteRequested = output<void>();
  /**
   * Lane chosen in the dropdown. Navigation-only (ASS-661): the parent
   * re-points the pager at this lane and opens a task in it; the current
   * task is never moved. Lane moves now live in the overflow context menu.
   */
  readonly navigateLane = output<string>();
  readonly moveToTop = output<void>();
  readonly pagerPrev = output<void>();
  readonly pagerNext = output<void>();
  /** Lane-action chosen via the primary button or the overflow menu. */
  readonly triageAction = output<TriageActionPayload>();
  readonly generateCommitMessage = output<void>();
  readonly addCommit = output<void>();

  /**
   * Tooltip text explaining the snapshot iteration, surfaced through the
   * app's canonical `[cacTooltip]` directive (single visual standard,
   * instant hover). Plain readable language, no embedded markup.
   */
  readonly pagerTooltip = computed(() => {
    const total = this.pagerTotal();
    if (total <= 0) return '';
    const lane = this.pagerLaneLabel() || 'this lane';
    const pos = this.pagerPosition();
    if (pos <= 0) {
      return `This task has left the ${lane} lane. ${total} job${total === 1 ? '' : 's'} remain in the captured iteration.`;
    }
    return `Iterating jobs in the ${lane} lane. Showing job ${pos} of ${total} captured when you entered this view.`;
  });

  /**
   * Lane dropdown options. The dropdown is navigation-only: each entry is a
   * lane the pager can step through, in kanban left-to-right order. The
   * orchestrator-controlled lanes (`3-progress`, `4-auto-review`) are omitted
   * — they are not manual navigation targets, matching the context menu that
   * also refuses them as move targets. The retired `1a-orchestrator-prep`
   * lane is omitted too (prep runs in-place on 1-preparation now).
   */
  readonly laneOptions: readonly { state: string; label: string }[] = [
    TaskState.Preparation,
    TaskState.Ready,
    TaskState.HumanReview,
    TaskState.Escalated,
    TaskState.Completed,
    TaskState.Archive,
  ].map((state) => ({ state, label: laneShortName(state) }));

  isStandardLane(state: string): boolean {
    return this.laneOptions.some(o => o.state === state);
  }

  /** "Do Next" only makes sense while the task is queued in 2-ready and not yet
   *  picked up. The state-select dropdown is the path to bring it into ready
   *  from a different lane first; after that the button surfaces. */
  readonly canMoveToTop = computed(() => this.info().state === TaskState.Ready);

  /** Lane the dropdown shows as selected (pager lane, fallback to job state). */
  readonly selectedLane = computed(() => this.pagerLaneState() || this.info().state);

  onStateSelect(event: Event) {
    const target = event.target as HTMLSelectElement;
    const next = target.value;
    const current = this.selectedLane();
    // Navigation-only: re-sync the native control to the lane the pager
    // actually iterates after we (maybe) navigate. `navigateLane` triggers
    // a synchronous snapshot re-capture in the parent, so by the time this
    // microtask runs `selectedLane()` already reflects the landed lane; when
    // navigation is declined (empty lane) it stays on `current`, so either
    // way the <select> snaps back to the real pager lane instead of the
    // user's transient pick.
    queueMicrotask(() => {
      const el = this.stateSelectEl?.nativeElement;
      if (el) el.value = this.selectedLane();
    });
    if (!next || next === current) return;
    this.navigateLane.emit(next);
  }

  // --- triage cluster (primary + overflow) --------------------------------

  /** Lane label for tooltips / aria-text on the overflow trigger. */
  readonly triageLaneLabel = computed(() => laneLabelFor(this.info().state));

  /** Index 0 of the lane's action list when it carries a primary variant. */
  readonly triagePrimary = computed<TriageButton | null>(() =>
    primaryActionFor(this.info().state),
  );

  /**
   * State-dependent presentation for the Human Review acceptance primary. Null
   * for every other primary (Run now, Stop run, ...). When the work has already
   * landed it carries the landed-status pill text and relabels the button to
   * "Accept"; otherwise the offer stays "Merge into Develop".
   */
  readonly mergeAcceptView = computed<MergeAcceptView | null>(() => {
    const p = this.triagePrimary();
    if (!p || p.id !== 'mark-done') return null;
    return mergeAcceptViewFor(this.info(), this.landedState());
  });

  /** Effective primary label (state-aware for the Human Review acceptance). */
  readonly primaryLabel = computed(() => {
    const p = this.triagePrimary();
    if (!p) return '';
    return this.mergeAcceptView()?.acceptLabel ?? p.label;
  });

  /**
   * Primary-action ids whose label and effect depend on the live git landed
   * status. The Human Review acceptance (`mark-done`) is the sole one today: it
   * reads "Merge into Develop" vs "Accept" off `landedState`, so it must not act
   * (or show a guessed label) while the git status is still loading (AGT-2006).
   */
  private readonly GIT_DEPENDENT_PRIMARY_IDS: ReadonlySet<string> = new Set(['mark-done']);

  /**
   * True when the current primary depends on git status that is still loading.
   * Drives the button's disabled + skeleton state so the acceptance action is
   * held back until the branch/merge truth resolves, then switches atomically.
   */
  readonly primaryAwaitingGit = computed(() => {
    const p = this.triagePrimary();
    return !!p && this.GIT_DEPENDENT_PRIMARY_IDS.has(p.id) && this.gitInfoLoading();
  });

  /** Remaining lane actions + always-on Edit/Delete fallbacks. */
  readonly triageOverflow = computed<TriageButton[]>(() =>
    overflowActionsFor(this.info().state),
  );

  /** True when a primary or any overflow action is available. */
  readonly hasTriageActions = computed(
    () => this.triagePrimary() !== null || this.triageOverflow().length > 0 || this.commitActionsAvailable(),
  );

  /**
   * Counter shown on the overflow button (and read by E2E specs to verify
   * the panel is anchored to the right lane). Mirrors the wording of the
   * old footer-bar counter so legacy spec assertions still pass.
   */
  readonly triageCounterText = computed(() => {
    const total = this.laneSize();
    const lane = this.triageLaneLabel();
    if (total <= 0) return `in ${lane}`;
    const pos = Math.max(this.laneIndex() + 1, 1);
    return `Task ${pos} of ${total} in ${lane}`;
  });

  readonly triageOverflowOpen = signal(false);
  readonly triageOverflowAnchor = signal<HTMLElement | null>(null);

  readonly triageMenuItems = computed<MenuItem[]>(() => {
    const disabled = this.mutationsBlocked() || this.triageActingId() !== null;
    const items = this.triageOverflow().map<MenuItem>(b => ({
      kind: 'row',
      id: b.id,
      label: b.label,
      danger: b.variant === 'danger',
      disabled,
    }));
    if (this.commitActionsAvailable()) {
      if (items.length > 0) items.push({ kind: 'separator' });
      items.push(
        {
          kind: 'row',
          id: 'generate-commit-message',
          label: this.generatingCommitMessage() ? 'Generating Commit Message...' : 'Generate Commit Message',
          disabled: disabled || this.generatingCommitMessage() || this.committing(),
        },
        {
          kind: 'row',
          id: 'add-commit',
          label: this.committing() ? 'Committing...' : 'Add Commit...',
          hint: this.commitMessageDraft().trim() ? 'Draft ready' : undefined,
          disabled: disabled || this.generatingCommitMessage() || this.committing(),
        },
      );
    }
    return items;
  });

  primaryTooltip(): string {
    const p = this.triagePrimary();
    if (!p) return '';
    if (this.primaryAwaitingGit()) return 'Checking git status — action available once loaded.';
    if (this.mutationsBlocked()) return 'Update in progress — actions paused.';
    const label = this.primaryLabel();
    if (this.triageActingId() === p.id) return `${label}…`;
    const merge = this.mergeAcceptView();
    if (merge?.landed && merge.statusTooltip) return `${merge.statusTooltip} (Enter)`;
    return `${label} (Enter)`;
  }

  overflowTooltip(): string {
    if (this.mutationsBlocked()) return 'Update in progress — actions paused.';
    const count = this.triageMenuItems().filter(i => i.kind === 'row').length;
    return `${this.triageLaneLabel()} actions (${count})`;
  }

  onPrimaryClick(): void {
    const p = this.triagePrimary();
    if (!p) return;
    // Hold git-dependent primaries until the branch/merge status has loaded, so
    // Enter / click cannot trigger an acceptance while the label is still a guess.
    if (this.primaryAwaitingGit()) return;
    this.emitTriage(p);
  }

  toggleTriageOverflow(event: MouseEvent): void {
    event.stopPropagation();
    if (this.mutationsBlocked()) return;
    this.triageOverflowAnchor.set(event.currentTarget as HTMLElement);
    this.triageOverflowOpen.update(v => !v);
  }

  closeTriageOverflow(): void {
    this.triageOverflowOpen.set(false);
  }

  onTriageMenuItemClick(ev: MenuItemClickEvent): void {
    if (ev.id === 'generate-commit-message') {
      this.triageOverflowOpen.set(false);
      this.generateCommitMessage.emit();
      return;
    }
    if (ev.id === 'add-commit') {
      this.triageOverflowOpen.set(false);
      this.addCommit.emit();
      return;
    }
    const button = this.triageOverflow().find(b => b.id === ev.id);
    if (!button) return;
    // Delete keeps its dedicated output (boardMutations.deleteFromDetail
    // owns the confirm dialog + pager-aware advance). The rest of the lane
    // actions flow through the triage controller via `triageAction`.
    if (button.id === 'delete') {
      this.triageOverflowOpen.set(false);
      this.deleteRequested.emit();
      return;
    }
    this.emitTriage(button);
  }

  /** Called by the parent on Enter when no input is focused. */
  triggerPrimary(): void {
    this.onPrimaryClick();
  }

  private emitTriage(button: TriageButton): void {
    if (this.mutationsBlocked() || this.triageActingId() !== null) return;
    this.triageOverflowOpen.set(false);
    this.triageAction.emit({ id: button.id, label: button.label, intent: button.intent });
  }

  @ViewChild('stateSelect') private stateSelectEl?: ElementRef<HTMLSelectElement>;

  /**
   * Keep the lane dropdown's DOM value pinned to the pager lane. Angular's
   * [value] binding skips the DOM write when the bound string is unchanged
   * between two jobs in the same lane (e.g. paging through 2-ready), which
   * would otherwise leave a user's transient `selectOption` choice on screen.
   * The effect re-asserts the value on every selected-lane change.
   */
  private syncStateSelect = effect(() => {
    const lane = this.selectedLane();
    const el = this.stateSelectEl?.nativeElement;
    if (el && el.value !== lane) {
      queueMicrotask(() => { if (el) el.value = lane; });
    }
  });

  @ViewChild('titleInput') private titleInputEl?: ElementRef<HTMLInputElement>;

  /** Auto-focus the input when editing turns on (parity with prior behavior). */
  private focusOnEdit = effect(() => {
    if (this.editingTitle()) {
      queueMicrotask(() => this.titleInputEl?.nativeElement.select());
    }
  });

  private readonly nowTick = inject(NowTickService).now;

  readonly relativeCreated = computed(() => fmtRelativeShort(this.info().createdAt, this.nowTick()));
  readonly createdAtTooltip = computed(() => fmtDateTime(this.info().createdAt));

  readonly identity = computed(() => projectIdentity(this.info().projectName));

  /**
   * AGT-2069 — prominent planning/research badge for the detail header. Only
   * non-coding modes render one, so the header stays quiet for the common case
   * while a planning-task detail is unmistakably marked "here work is PLANNED".
   * Glyph + label come from the same source as the board card + create picker.
   */
  readonly modeBadge = computed(() => {
    const mode = this.info().mode;
    if (mode !== 'planning' && mode !== 'research' && mode !== 'concept') return null;
    return {
      mode,
      icon: taskModeIcon(mode),
      label: taskModeLabel(mode),
      tooltip:
        mode === 'planning'
          ? 'Planning task: read-only. It investigates and proposes the next work; it is only done once it spawns follow-up cards or declares no follow-up intended.'
          : mode === 'research'
            ? 'Research task: read-only with web access. It gathers information and reports findings.'
            : 'Concept task: one docs-only Dossier awaiting human review before card promotion.',
    };
  });

  stateLabel(state: string): string { return fmtStateLabel(state); }

  // Title right-click context menu for copy actions
  private readonly notifs = inject(NotificationService);
  readonly titleContextMenu = signal<{ x: number; y: number } | null>(null);
  readonly titleCtxMenuItems = computed<readonly MenuItem[]>(() => {
    const info = this.info();
    const items: MenuItem[] = [
      { kind: 'row', id: 'copy-name', label: 'Copy Name' },
      { kind: 'row', id: 'copy-id', label: 'Copy ID' },
    ];
    if (info.key) {
      items.push({ kind: 'row', id: 'copy-key', label: `Copy Key (${info.key})` });
    }
    return items;
  });

  openTitleContextMenu(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.titleContextMenu.set({ x: event.clientX, y: event.clientY });
  }

  closeTitleContextMenu(): void {
    this.titleContextMenu.set(null);
  }

  onTitleCtxMenuItemClick(ev: MenuItemClickEvent): void {
    const info = this.info();
    let text = '';
    let label = '';
    if (ev.id === 'copy-name') { text = info.title || info.id; label = 'Name'; }
    else if (ev.id === 'copy-id') { text = info.id; label = 'ID'; }
    else if (ev.id === 'copy-key' && info.key) { text = info.key; label = 'Key'; }
    if (text) {
      copyTextToClipboard(text).then(ok => {
        if (ok) this.notifs.success(`${label} copied`);
      });
    }
  }
}
