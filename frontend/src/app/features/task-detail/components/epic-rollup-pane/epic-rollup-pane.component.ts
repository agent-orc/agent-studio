import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import type { CliType, EpicRollup, EpicSubTaskRef, TaskInfo } from '../../../../models/task.model';
import type { CliModelInfo } from '../../../cli';
import { TaskService } from '../../../../services/task.service';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { MarkdownRichEditorComponent } from '../../../../components/markdown-rich-editor/markdown-rich-editor';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';
import { ReferencesSectionComponent } from '../references-section/references-section.component';
import { LANE_LABELS } from '../../state/lane-pager.service';
import { laneName } from '../../../../models/lane-presentation';

/** One lane column in the epic mini-board: a state plus the sub-tasks that sit in it. */
export interface EpicLaneGroup {
  state: string;
  label: string;
  subTasks: EpicSubTaskRef[];
}

/** Canonical kanban lane order; `LANE_LABELS` is authored in that order. */
const LANE_ORDER = Object.keys(LANE_LABELS);

/**
 * Epic detail pane: shown in the task-detail view when the open card is an
 * epic (kind=epic). Two halves in one card:
 *
 *  - Edit: the epic's own properties - title, description (prompt.md, the
 *    planning brief that drives decomposition), cross-references, and model/CLI
 *    - are editable inline and persist through the same API endpoints the
 *    regular task-edit surfaces use. The title routes through the host's
 *    setJobTitle PUT, the same one the detail header / Overview hero use.
 *  - Status: the live sub-task progress from GET /api/epics/{id} as a
 *    full-width mini-board, grouped into the lane/state columns the sub-tasks
 *    currently sit in, so a glance shows where the epic's work stands.
 *
 * Sub-task assignment is not done here - that happens on the cards (way 2) or
 * in the create dialog (way 1); clicking a sub-task opens its detail.
 */
@Component({
  selector: 'app-epic-rollup-pane',
  standalone: true,
  imports: [TooltipDirective, MarkdownRichEditorComponent, MarkdownViewComponent, CliModelSelectorComponent, ReferencesSectionComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './epic-rollup-pane.component.html',
  styleUrl: './epic-rollup-pane.component.scss',
})
export class EpicRollupPaneComponent {
  readonly epicId = input.required<string>();
  readonly watchPath = input<string>('');
  /** Full epic record - feeds the references section and the model/CLI picker. */
  readonly job = input<TaskInfo | null>(null);
  /** The epic's description (prompt.md body), already loaded by the parent. */
  readonly promptMarkdown = input<string>('');
  /** Model catalog snapshot forwarded to the CLI/model picker. */
  readonly availableModels = input<readonly CliModelInfo[]>([]);
  /** True while a CLI run is in flight: gates description + model edits. */
  readonly isRunning = input(false);
  /** Sub-task currently open in the host's master/detail area. */
  readonly selectedSubTaskId = input<string | null>(null);

  /** Bubbles a click on a sub-task so the host opens its detail. */
  readonly openSubTask = output<{ jobId: string; watchPath: string }>();
  /** Edited epic title; the host persists it via the shared setJobTitle PUT. */
  readonly saveTitle = output<string>();
  /** Edited description body; the host writes it to prompt.md via the API. */
  readonly saveDescription = output<string>();
  /** Fires after a successful reference write so the host can re-fetch. */
  readonly referencesChanged = output<void>();
  /** Atomic CLI + model commit forwarded to the host's sequenced PUTs. */
  readonly agentConfigCommit = output<{ cliType: CliType; model: string; thinkingLevel: string | null }>();

  private readonly jobs = inject(TaskService);
  readonly rollup = signal<EpicRollup | null>(null);
  readonly loading = signal(false);
  readonly error = signal(false);
  /** Flips the description block between rendered view and the rich editor. */
  readonly editingDesc = signal(false);
  /** Flips the title row between the rendered title and the inline input. */
  readonly editingTitle = signal(false);
  /** Working copy of the title while the inline input is open. */
  readonly titleDraft = signal('');

  /** Completed share of the epic, 0-100, for the progress bar width. */
  readonly progressPct = computed(() => {
    const r = this.rollup();
    if (!r || r.subTaskTotal === 0) return 0;
    return Math.round((r.completed / r.subTaskTotal) * 100);
  });

  /**
   * Sub-tasks bucketed into the lanes they currently sit in, in kanban order.
   * Empty lanes are dropped so the board only shows lanes that hold work;
   * unknown/legacy states are appended after the canonical ones.
   */
  readonly laneGroups = computed<EpicLaneGroup[]>(() => {
    const r = this.rollup();
    if (!r) return [];
    const byState = new Map<string, EpicSubTaskRef[]>();
    for (const sub of r.subTasks) {
      const bucket = byState.get(sub.state);
      if (bucket) bucket.push(sub);
      else byState.set(sub.state, [sub]);
    }
    const known = LANE_ORDER.filter((s) => byState.has(s));
    const unknown = [...byState.keys()].filter((s) => !LANE_ORDER.includes(s)).sort();
    return [...known, ...unknown].map((state) => ({
      state,
      label: LANE_LABELS[state] ?? this.laneLabel(state),
      subTasks: [...byState.get(state)!].sort((a, b) => a.order - b.order),
    }));
  });

  constructor() {
    // Re-fetch whenever the bound epic changes (lane pager swaps the open card).
    effect(() => {
      const id = this.epicId();
      const wp = this.watchPath();
      if (!id) return;
      this.loading.set(true);
      this.error.set(false);
      this.jobs.getEpic(id, wp || undefined).subscribe({
        next: (r) => { this.rollup.set(r); this.loading.set(false); },
        error: () => { this.error.set(true); this.loading.set(false); },
      });
    });

    // Drop the open editors when the lane pager swaps to a different epic so we
    // never show one epic's draft against another's description/title.
    effect(() => {
      this.epicId();
      this.editingDesc.set(false);
      this.editingTitle.set(false);
    }, { allowSignalWrites: true });
  }

  beginEditTitle(): void {
    const info = this.job();
    this.titleDraft.set(info ? (info.title || info.id) : '');
    this.editingTitle.set(true);
  }

  cancelEditTitle(): void {
    this.editingTitle.set(false);
  }

  onTitleSave(): void {
    const trimmed = this.titleDraft().trim();
    this.editingTitle.set(false);
    if (!trimmed) return;
    const info = this.job();
    if (info && trimmed === (info.title || info.id)) return;
    this.saveTitle.emit(trimmed);
  }

  beginEditDesc(): void {
    if (this.isRunning()) return;
    this.editingDesc.set(true);
  }

  cancelEditDesc(): void {
    this.editingDesc.set(false);
  }

  onDescSave(content: string): void {
    this.saveDescription.emit(content);
    this.editingDesc.set(false);
  }

  /** Lane display name from the one catalogue (AGT-2715). */
  laneLabel(state: string): string {
    return laneName(state);
  }

  openSub(sub: EpicSubTaskRef): void {
    this.openSubTask.emit({ jobId: sub.id, watchPath: this.watchPath() });
  }

  trackByLane = (_: number, lane: EpicLaneGroup) => lane.state;
}
