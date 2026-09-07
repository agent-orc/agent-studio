import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TaskService } from '../../../../services/task.service';
import { LANES_IN_BOARD_ORDER } from '../../../../models/lane-presentation';
import type { PipelineCatalogueStep, PipelineStepSetting } from '../../../../features/task-pipeline';
import { TooltipDirective } from 'coding-agent-chat/shared';
import {
  SORTABLE_LANES,
  USER_VISIBLE_LANE_SORT_STRATEGIES,
  laneSortStrategyMeta,
} from '../../../../services/lane-sort.util';

type AutoPushStrategy = 'never' | 'on-completed' | 'always-immediate';

/** One workflow lane with a one-line role description, in board order. */
interface WorkflowLaneRow {
  state: string;
  label: string;
  icon: string;
  role: string;
}

/** A read-only transition card: what the platform does today at one hop. */
interface TransitionRow {
  /** Stable key for tracking + data-testid. */
  key: string;
  /** Lane hop, e.g. `3-progress -> 4-auto-review`. */
  hop: string;
  /** What the platform does, named after the task's three facets. */
  facet: 'auto-commit' | 'attribution' | 'gates' | 'auto-push';
  facetLabel: string;
  /** Live effective state (on/off, strategy, gate count) for this facet. */
  state: string;
  /** One-line description of the implemented behaviour. */
  detail: string;
  /** Where the operator configures this today (read-only here). */
  configuredIn: string;
}

/**
 * Workflow / Lanes page — stage 1 (T6a, nav-rebuild step 3).
 *
 * Read-mostly transparency surface for the lane model and what the platform
 * does at each transition *today*:
 *  - one board-order lane list with each lane's role and sort-order control,
 *  - a read-only transition view (auto-commit, attribution, gates, auto-push)
 *    driven by the project's live settings + pipeline config,
 *  - placeholder sections for stage 2/3 that land only after the Git-concept
 *    decisions (docs/concepts/git-branching-integration-zielbild.md §7).
 *
 * Leitplanke: this page visualises *implemented* behaviour only. The
 * per-transition Git profiles / configurable gates are deliberately a
 * placeholder, not a control, until the semantics are decided.
 */
@Component({
  selector: 'app-project-workflow-section',
  standalone: true,
  imports: [FormsModule, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-workflow-section.html',
  styleUrl: './project-workflow-section.scss',
})
export class ProjectWorkflowSectionComponent implements OnInit {
  readonly projectName = input.required<string>();

  private readonly jobService = inject(TaskService);

  // ---- Lane list (read-only, board order) ----
  readonly lanes: readonly WorkflowLaneRow[] = SORTABLE_LANES.map((lane) => ({
    state: lane.state,
    label: lane.label,
    icon: lane.icon,
    role: LANE_ROLES[lane.state] ?? '',
  }));

  // ---- Board sort per lane (the only writeable controls; shown in the lane list) ----
  readonly sortableLanes = SORTABLE_LANES;
  readonly laneSortOptions = USER_VISIBLE_LANE_SORT_STRATEGIES;
  /** Per-lane sort-strategy selection, keyed by lane state. */
  readonly laneSortDraft: Record<string, string> = {};
  laneSortMeta(strategy: string | null | undefined) {
    return laneSortStrategyMeta(strategy);
  }

  // ---- Live settings backing the read-only transition view ----
  private readonly autoCommit = signal<boolean | null>(null);
  private readonly autoPushStrategy = signal<AutoPushStrategy | null>(null);
  private readonly pipelineCatalogue = signal<readonly PipelineCatalogueStep[]>([]);
  private readonly pipelineOverrides = signal<Record<string, PipelineStepSetting>>({});

  /**
   * Steps that can gate the Post-Processing (4-auto-review) lane today: any
   * enabled step that exposes a gate mode (aspects, code-review grade,
   * orchestrator decision, ...). Derived live from the catalogue + overrides
   * so the count reflects this project's actual configuration.
   */
  readonly gateSteps = computed(() => {
    const overrides = this.pipelineOverrides();
    return this.pipelineCatalogue()
      .filter((step) => step.supportsMode)
      .filter((step) => overrides[step.id]?.enabled ?? step.defaultEnabled)
      .map((step) => step.displayName);
  });

  readonly transitions = computed<readonly TransitionRow[]>(() => {
    const commit = this.autoCommit();
    const push = this.autoPushStrategy();
    const gates = this.gateSteps();
    return [
      {
        key: 'auto-commit',
        hop: '3-progress → 4-auto-review',
        facet: 'auto-commit',
        facetLabel: 'Auto-commit',
        state: commit == null ? '…' : commit ? 'On' : 'Off',
        detail:
          'When on, the orchestrator commits the run’s changes as the task leaves In Progress.',
        configuredIn: 'Settings',
      },
      {
        key: 'attribution',
        hop: '3-progress → 4-auto-review',
        facet: 'attribution',
        facetLabel: 'Attribution',
        state: commit == null ? '…' : commit ? 'SHA stamped' : 'No commit',
        detail:
          'The commit SHA is stamped on the task (TaskInfo.Commit) so the run’s work is traceable. Only happens when auto-commit is on.',
        configuredIn: 'Settings',
      },
      {
        key: 'gates',
        hop: '4-auto-review (Post Processing)',
        facet: 'gates',
        facetLabel: 'Gates',
        state:
          gates.length === 0
            ? 'No active gate steps'
            : `${gates.length} active gate step${gates.length === 1 ? '' : 's'}`,
        detail:
          'The configured review pipeline runs here (aspects, code-review grade, orchestrator decision) and resolves to accept / reissue / escalate.',
        configuredIn: 'Pipeline',
      },
      {
        key: 'auto-push',
        hop: '→ 6-completed',
        facet: 'auto-push',
        facetLabel: 'Auto-push',
        state: push == null ? '…' : AUTO_PUSH_LABELS[push],
        detail:
          'On completed pushes git push origin <sha>:refs/heads/main (never force). Immediate also pushes right after auto-commit.',
        configuredIn: 'Settings',
      },
    ];
  });

  ngOnInit(): void {
    const project = this.projectName();

    this.jobService.getLaneSortStrategies(project).subscribe({
      next: (res) => {
        const resolved = res.resolved ?? {};
        for (const lane of this.sortableLanes) {
          this.laneSortDraft[lane.state] = resolved[lane.state] ?? 'manual';
        }
      },
      error: () => {
        for (const lane of this.sortableLanes) {
          if (!(lane.state in this.laneSortDraft)) this.laneSortDraft[lane.state] = 'manual';
        }
      },
    });

    this.jobService.getProjectSnapshot(project).subscribe({
      next: (snap) => {
        this.autoCommit.set(snap.settings.autoCommit);
        this.autoPushStrategy.set(snap.settings.autoPushStrategy);
      },
      error: () => { /* leave the transition state as "…" */ },
    });

    this.jobService.getPipelineCatalogue().subscribe({
      next: (cat) => this.pipelineCatalogue.set(cat.steps ?? []),
      error: () => { /* gate count just stays at zero */ },
    });
    this.jobService.getAllProjectSettings().subscribe({
      next: (all) => this.pipelineOverrides.set(all[project]?.pipelineSteps ?? {}),
      error: () => { /* keep empty overrides */ },
    });
  }

  /** Persist one lane's sort strategy, then re-read the resolved map. */
  onLaneSortChange(lane: string): void {
    const strategy = this.laneSortDraft[lane] ?? 'manual';
    this.jobService.setLaneSortStrategy(this.projectName(), lane, strategy).subscribe({
      next: (res) => { this.laneSortDraft[lane] = res.strategy ?? strategy; },
      error: () => { /* keep the optimistic draft; next open re-reads */ },
    });
  }
}

/**
 * Role copy per lane, keyed by canonical lane state. Board order via
 * SORTABLE_LANES. The sentences are the shared ones from
 * `models/lane-presentation.ts`, so this section explains a lane in the same
 * words the Result header and the lane guides use.
 */
const LANE_ROLES: Record<string, string> = Object.fromEntries(
  LANES_IN_BOARD_ORDER.map((lane) => [lane.state, lane.sentence]),
);

const AUTO_PUSH_LABELS: Record<AutoPushStrategy, string> = {
  'never': 'Never',
  'on-completed': 'On completed',
  'always-immediate': 'Immediate',
};
