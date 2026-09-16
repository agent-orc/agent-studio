import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TaskService } from '../../../../services/task.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { ProjectQueueHealth, RunnerStatus } from '../../../../models/task.model';
import { CLI_TYPES, type CliType } from '../../../../models/task.model';
import { cliTypeLabel, cliTypeIcon } from '../../../../services/format.util';
import type { OrchestratorLogEntry, OrchestratorSession } from '../../../../features/orchestrator';
import { CliCatalogStore } from '../../../cli';
import { TokenSummaryBlockComponent } from '../../../../features/tokens';
import { GlobalOrchestratorCardComponent } from '../../../../features/orchestrator';
import { ProjectArchitectureSectionComponent } from '../project-architecture-section/project-architecture-section';
import { ProjectDriftSectionComponent } from '../project-drift-section/project-drift-section';
import { ProjectDriftOverviewSectionComponent } from '../project-drift-overview-section/project-drift-overview-section';
import { ProjectSupervisorSectionComponent } from '../project-supervisor-section/project-supervisor-section';
import { ProjectMetaCycleSectionComponent } from '../project-meta-cycle-section/project-meta-cycle-section';
import { ProjectAnalysisReportsSectionComponent } from '../project-analysis-reports-section/project-analysis-reports-section';
import { ProjectWorkspaceSectionComponent } from '../project-workspace-section/project-workspace-section';
import { AutonomySliderComponent } from '../autonomy-slider/autonomy-slider';
import { AnalysisReport } from '../../../../models/analysis-report.model';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { ProjectWorkflowSectionComponent } from '../project-workflow-section/project-workflow-section';
import { ProjectCliEnvironmentSectionComponent } from '../project-cli-environment-section/project-cli-environment-section';
interface ProjectSettingsRow {
  autoCommit: boolean;
  crashRecoveryEnabled: boolean;
  autoPushStrategy: AutoPushStrategy;
  runnerMode: string | null;
  orchestratorModel: string | null;
  /** AGT-2839: project override; null inherits the safe default. */
  integrationGateReviewReuse: boolean | null;
  /** AGT-2839: the value the integration gate actually applies. */
  integrationGateReviewReuseEffective: boolean;
}

/** Bound value of the integration-gate reuse select. */
type IntegrationGateReuseChoice = 'inherit' | 'enabled' | 'disabled';

type AutoPushStrategy = 'never' | 'on-completed' | 'always-immediate';

export type ProjectDetailView =
  | 'overview'
  | 'jobs'
  | 'settings'
  // Nav-rebuild step 2 (T5b): sections that used to live inside the 'settings'
  // view now render under their own view so the project-shell rails (Workflow)
  // and the workspace Admin → CLI & Modelle surface can mount them unchanged.
  // Same controls, same backend writes — only the mount location moved.
  | 'workflow'
  | 'cli'
  | 'orchestrator'
  | 'activity'
  | 'architecture'
  | 'drift'
  | 'observability';

/**
 * Project detail panel: name + paths, runner mode toggle, orchestrator
 * model selector, auto-commit toggle, job-state counts, and the most recent
 * orchestrator entries with token totals. Mounted as an overlay panel from
 * the project-tabs settings button.
 *
 * Read-mostly: only the three setting controls write back. Everything
 * else is polled (5s interval) so a backend change made elsewhere is
 * reflected without manual refresh.
 */
@Component({
  selector: 'app-project-detail',
  standalone: true,
  imports: [
    FormsModule,
    TokenSummaryBlockComponent,
    GlobalOrchestratorCardComponent,
    ProjectArchitectureSectionComponent,
    ProjectDriftSectionComponent,
    ProjectDriftOverviewSectionComponent,
    ProjectSupervisorSectionComponent,
    ProjectMetaCycleSectionComponent,
    ProjectAnalysisReportsSectionComponent,
    ProjectWorkspaceSectionComponent,
    ProjectWorkflowSectionComponent,
    ProjectCliEnvironmentSectionComponent,
    AutonomySliderComponent,
    TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-detail.html',
  styleUrl: './project-detail.scss'
})
export class ProjectDetailComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();
  readonly view = input<ProjectDetailView>('overview');
  readonly openFeed = output<string>();
  readonly openReport = output<AnalysisReport>();

  private readonly jobService = inject(TaskService);
  private readonly cliCatalog = inject(CliCatalogStore);

  readonly settings = signal<ProjectSettingsRow | null>(null);
  readonly runnerStatus = signal<RunnerStatus | null>(null);
  readonly recentEntries = signal<OrchestratorLogEntry[]>([]);
  readonly orchSession = signal<OrchestratorSession | null>(null);
  readonly projectPaths = signal<{ path: string; rootPath: string | null; repositoryPath: string | null } | null>(null);
  readonly pendingDecisions = signal<readonly { jobId: string; title: string; reason: string | null }[]>([]);
  readonly queueHealth = signal<ProjectQueueHealth | null>(null);
  readonly queueRepairBusy = signal(false);
  readonly queueRepairMessage = signal<string | null>(null);

  readonly livePendingDecisions = signal<readonly { jobId: string; title: string; kind: string; reason: string | null; detectedAt: string }[]>([]);
  readonly liveReplyDrafts: Record<string, string> = {};
  readonly liveReplySending: Record<string, boolean> = {};
  readonly liveReplyErrors: Record<string, string | null> = {};

  autoCommitDraft = false;
  crashRecoveryDraft = true;
  autoPushStrategyDraft: AutoPushStrategy = 'always-immediate';
  integrationGateReuseDraft: IntegrationGateReuseChoice = 'inherit';
  orchModelDraft = '';

  // Per-CLI permission/sandbox mode (YOLO default). One row per CLI shows the
  // effective mode + where it came from (project override / global config /
  // platform default), with a dropdown to override and a reset-to-default.
  // Defaults to YOLO so orchestrated pipeline runs never hang on a prompt.
  readonly cliTypes = CLI_TYPES;
  readonly cliModeResolved = signal<Record<string, { mode: string; source: string; args: string[] }>>({});
  readonly cliModeAvailable = signal<readonly string[]>(['yolo', 'workspace-write', 'read-only', 'custom']);
  /** Bound select value per CLI; mirrors the resolved effective mode until the operator picks one. */
  readonly cliModeDraft: Record<string, string> = {};
  /** Per-CLI write in flight; disables that row's controls until the PUT resolves. */
  readonly cliModeBusy: Record<string, boolean> = {};

  readonly cliModeMeta: Record<string, { label: string; hint: string }> = {
    'yolo': { label: 'YOLO', hint: 'Maximum autonomy — skips every permission/sandbox prompt. Default for agent-orchestrated runs.' },
    'workspace-write': { label: 'Workspace-Write', hint: 'Agent may write inside the workspace but is sandboxed from the wider system.' },
    'read-only': { label: 'Read-only', hint: 'Agent may read and plan but not write (plan-mode for Claude, read-only sandbox for Codex).' },
    'custom': { label: 'Custom', hint: 'Inject no permission flags — the CLI obeys whatever its own config files dictate.' },
  };
  readonly cliSourceMeta: Record<string, { label: string; hint: string }> = {
    'project': { label: 'project override', hint: 'Set explicitly for this project — overrides global config and the platform default.' },
    'global': { label: 'global config', hint: 'Inherited from the CLI’s own global config file (e.g. ~/.codex/config.toml).' },
    'default': { label: 'platform default', hint: 'Platform default (YOLO) — no project override and no global config detected.' },
  };

  cliModeLabel(mode: string | null | undefined) { return this.cliModeMeta[mode ?? '']?.label ?? mode ?? ''; }
  cliModeHint(mode: string | null | undefined) { return this.cliModeMeta[mode ?? '']?.hint ?? ''; }
  cliSourceLabel(source: string | null | undefined) { return this.cliSourceMeta[source ?? '']?.label ?? source ?? ''; }
  cliSourceHint(source: string | null | undefined) { return this.cliSourceMeta[source ?? '']?.hint ?? ''; }

  /** One row per CLI: effective mode + source + the args the next spawn will inject. */
  readonly cliModeRows = computed(() => {
    const resolved = this.cliModeResolved();
    return this.cliTypes.map((cli) => {
      const r = resolved[cli];
      return {
        cliType: cli as string,
        label: cliTypeLabel(cli as CliType),
        icon: cliTypeIcon(cli as CliType),
        mode: r?.mode ?? 'yolo',
        source: r?.source ?? 'default',
        args: r?.args ?? [],
      };
    });
  });

  readonly cliEnvironmentModeRows = computed(() => this.cliModeRows().map(row => ({
    cliType: row.cliType,
    mode: row.mode,
    source: this.cliSourceLabel(row.source),
  })));

  // T1b / ASS-1742: per-project CLI context mode (clean isolated home vs the
  // operator's shared global state). Default CLEAN for reproducible coding
  // runs. Shared-only CLIs (Copilot, Gemini) render the dropdown disabled and
  // pinned to shared since they expose no config-home redirect.
  readonly cliContextModeResolved = signal<Record<string, { mode: string; source: string; supported: boolean }>>({});
  readonly cliContextModeAvailable = signal<readonly string[]>(['clean', 'shared']);
  /** Bound select value per CLI; mirrors the resolved effective context mode. */
  readonly cliContextModeDraft: Record<string, string> = {};
  /** Per-CLI write in flight; disables that row's controls until the PUT resolves. */
  readonly cliContextModeBusy: Record<string, boolean> = {};

  readonly cliContextModeMeta: Record<string, { label: string; hint: string }> = {
    'clean': { label: 'Clean', hint: 'Isolierter Per-Run-Home: der Run sieht nur Prompt + versionierte Repo-Dateien — reproduzierbar, ohne alte Session-/Memory-Reste.' },
    'shared': { label: 'Shared', hint: 'Der globale CLI-Zustand des Operators (Session-Historie, Memory, Settings). Nur bewusst waehlen.' },
  };
  readonly cliContextSourceMeta: Record<string, { label: string; hint: string }> = {
    'project': { label: 'project override', hint: 'Explizit fuer dieses Projekt gesetzt — ueberschreibt den Plattform-Default.' },
    'default': { label: 'platform default', hint: 'Plattform-Default (clean) — kein Projekt-Override gesetzt.' },
  };

  cliContextModeLabel(mode: string | null | undefined) { return this.cliContextModeMeta[mode ?? '']?.label ?? mode ?? ''; }
  cliContextModeHint(mode: string | null | undefined) { return this.cliContextModeMeta[mode ?? '']?.hint ?? ''; }
  cliContextSourceLabel(source: string | null | undefined) { return this.cliContextSourceMeta[source ?? '']?.label ?? source ?? ''; }
  cliContextSourceHint(source: string | null | undefined) { return this.cliContextSourceMeta[source ?? '']?.hint ?? ''; }

  /** One row per CLI: effective context mode + source + whether clean is actually supported. */
  readonly cliContextModeRows = computed(() => {
    const resolved = this.cliContextModeResolved();
    return this.cliTypes.map((cli) => {
      const r = resolved[cli];
      return {
        cliType: cli as string,
        label: cliTypeLabel(cli as CliType),
        icon: cliTypeIcon(cli as CliType),
        mode: r?.mode ?? 'clean',
        source: r?.source ?? 'default',
        supported: r?.supported ?? false,
      };
    });
  });

  readonly cliEnvironmentContextModeRows = computed(() => this.cliContextModeRows().map(row => ({
    cliType: row.cliType,
    mode: row.mode,
    source: this.cliContextSourceLabel(row.source),
    supported: row.supported,
  })));

  /**
   * Mode buttons. The four runner modes are: manual (off), auto-single
   * (run one then revert), auto-continuous (run all ready), paused
   * (deny auto-pickup but keep state). Click sends a PUT and refreshes.
   */
  readonly modes: readonly { id: string; label: string; tooltip: string }[] = [
    { id: 'manual', label: 'Manual', tooltip: 'Auto-pickup off; user starts each task.' },
    { id: 'auto-single', label: 'Auto · single', tooltip: 'Pick up the next ready task once, then revert to manual.' },
    { id: 'auto-continuous', label: 'Auto · continuous', tooltip: 'Pick up ready tasks continuously.' },
    { id: 'paused', label: 'Paused', tooltip: 'Hold all auto-pickup; manual starts still allowed.' }
  ];

  readonly orchModelOptions = computed(() => [
    { id: '', label: 'Default' },
    ...this.cliCatalog.modelsFor('claude')
      .filter(model => model.available !== false)
      .map(model => ({ id: model.id, label: model.isDefault ? `Default (${model.label})` : model.label })),
  ]);
  readonly autoPushOptions: readonly { id: AutoPushStrategy; label: string; tooltip: string; isDefault?: boolean }[] = [
    {
      id: 'never',
      label: 'Never',
      tooltip: 'Do not push automatically; the operator pushes manually.'
    },
    {
      id: 'on-completed',
      label: 'On completed',
      tooltip: 'Push only after the job commit reaches 6-completed, after review.'
    },
    {
      id: 'always-immediate',
      label: 'Immediate',
      tooltip: 'Default. Push every platform-owned commit immediately; failures retry in the background.',
      isDefault: true
    }
  ];

  readonly effectiveMode = computed(() => {
    const status = this.runnerStatus();
    if (!status) return this.settings()?.runnerMode ?? 'manual';
    const proj = status.projects?.[this.projectName()];
    return proj?.mode ?? this.settings()?.runnerMode ?? 'manual';
  });

  readonly modeHint = computed(() => {
    switch (this.effectiveMode()) {
      case 'auto-continuous':
        return 'Auto-pickup is running. The runner will pick up the next ready task as soon as the current one finishes.';
      case 'auto-single':
        return 'After the next pickup, the runner reverts to Manual.';
      case 'paused':
        return 'Auto-pickup is held. Manual starts still work.';
      default:
        return 'No auto-pickup. You start each task manually.';
    }
  });

  readonly paths = computed(() => {
    return this.projectPaths() ?? {
      path: this.projectName(),
      rootPath: '',
      repositoryPath: ''
    };
  });

  readonly activeRunner = computed(() => this.runnerStatus()?.projects?.[this.projectName()] ?? null);

  queueHealthLabel(health: ProjectQueueHealth, emptyLabel: string, noun: string): string {
    if (health.issueCount === 0) return emptyLabel;
    return `${health.issueCount} ${noun}${health.issueCount === 1 ? '' : 's'}`;
  }

  readonly tokenTotalLabel = computed(() => {
    const entries = this.recentEntries();
    let input = 0, output = 0, count = 0;
    for (const e of entries) {
      if (!e.tokenUsage) continue;
      input += e.tokenUsage.inputTokens;
      output += e.tokenUsage.outputTokens;
      count++;
    }
    if (count === 0) return `${entries.length} entries; no orchestrator LLM calls yet.`;
    return `${entries.length} entries; ${count} orchestrator LLM call${count === 1 ? '' : 's'}: ↑${input.toLocaleString()} / ↓${output.toLocaleString()} tokens.`;
  });

  private pollTimer: VisibleIntervalHandle | null = null;

  ngOnInit(): void {
    this.cliCatalog.ensure('claude').subscribe({ error: () => void 0 });
    this.refreshAll();
    this.refreshCliModes();
    this.refreshCliContextModes();
    // Cycle 3: skip the 6-endpoint refreshAll fan-out when the panel is in
    // a backgrounded tab. The pre-Cycle-3 cadence put 42 requests over a
    // 10 s window from the project-detail panel alone (see logs/perf
    // baseline); this drops to zero when the tab is hidden.
    this.pollTimer = setVisibleInterval(() => this.refreshAll(true), 5_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearVisibleInterval(this.pollTimer);
    this.pollTimer = null;
  }

  refreshAll(silent = false): void {
    void silent;
    // Cycle 5: 1 round-trip instead of 6. /api/projects/{name}/snapshot
    // returns settings + runner-status + orchestrator-log-tail +
    // orchestrator-session + review-decisions-pending + runner-pending-
    // decisions all together. The standalone endpoints stay live for
    // other consumers (CLI usage sheet, board, etc.).
    this.jobService.getProjectSnapshot(this.projectName()).subscribe({
      next: (snap) => {
        const row = {
          autoCommit: snap.settings.autoCommit,
          crashRecoveryEnabled: snap.settings.crashRecoveryEnabled,
          autoPushStrategy: snap.settings.autoPushStrategy,
          runnerMode: snap.settings.runnerMode,
          orchestratorModel: snap.settings.orchestratorModel,
          integrationGateReviewReuse: snap.settings.integrationGateReviewReuse ?? null,
          integrationGateReviewReuseEffective:
            snap.settings.integrationGateReviewReuseEffective ?? false,
        };
        this.settings.set(row);
        if (this.autoCommitDraft !== row.autoCommit) this.autoCommitDraft = row.autoCommit;
        if (this.crashRecoveryDraft !== row.crashRecoveryEnabled) this.crashRecoveryDraft = row.crashRecoveryEnabled;
        if (this.autoPushStrategyDraft !== row.autoPushStrategy) this.autoPushStrategyDraft = row.autoPushStrategy;
        const wantedReuse: IntegrationGateReuseChoice =
          row.integrationGateReviewReuse === null
            ? 'inherit'
            : row.integrationGateReviewReuse
              ? 'enabled'
              : 'disabled';
        if (this.integrationGateReuseDraft !== wantedReuse) this.integrationGateReuseDraft = wantedReuse;
        const wantedModel = row.orchestratorModel ?? '';
        if (this.orchModelDraft !== wantedModel) this.orchModelDraft = wantedModel;

        // RunnerStatus signal expects the full RunnerStatus shape; the
        // snapshot only ships this project's slot, so wrap it. Other
        // projects' status comes from board polling and is irrelevant
        // here.
        if (snap.runnerStatus) {
          this.runnerStatus.set({ projects: { [this.projectName()]: snap.runnerStatus } });
        }

        this.recentEntries.set(snap.orchestratorLogTail ?? []);
        this.orchSession.set(snap.orchestratorSession ?? null);
        this.projectPaths.set(snap.paths ?? null);
        this.pendingDecisions.set(snap.reviewDecisionsPending ?? []);
        this.livePendingDecisions.set(snap.runnerPendingDecisions ?? []);
        this.queueHealth.set(snap.queueHealth ?? null);
      },
      error: () => { /* silent; keep last snapshot */ }
    });
  }

  /**
   * Send a reply to a live decision sentinel through the existing
   * /api/tasks/{jobId}/continue endpoint with mode 'steer'. The sentinel
   * resolves on the backend's next tick (the [user] log line cancels it),
   * which clears the banner without an explicit dismiss.
   */
  sendLiveDecisionReply(jobId: string): void {
    const text = (this.liveReplyDrafts[jobId] ?? '').trim();
    if (!text) return;
    this.liveReplySending[jobId] = true;
    this.liveReplyErrors[jobId] = null;
    this.jobService.continueJob(jobId, text, undefined, undefined, undefined, undefined, 'steer').subscribe({
      next: () => {
        this.liveReplyDrafts[jobId] = '';
        this.liveReplySending[jobId] = false;
        // Optimistically clear the banner; the next refresh tick will
        // re-confirm from the backend.
        this.livePendingDecisions.set(
          this.livePendingDecisions().filter(p => p.jobId !== jobId)
        );
        this.refreshAll(true);
      },
      error: (err) => {
        this.liveReplySending[jobId] = false;
        this.liveReplyErrors[jobId] = err?.error?.error || err?.message || 'Failed to send reply.';
      }
    });
  }

  setMode(mode: string): void {
    this.jobService.setRunnerMode(this.projectName(), mode).subscribe({
      next: () => this.refreshAll(true),
      error: () => this.refreshAll(true)
    });
  }

  onAutoCommitChange(): void {
    this.jobService.setProjectAutoCommit(this.projectName(), this.autoCommitDraft).subscribe({
      next: () => this.refreshAll(true),
      error: () => this.refreshAll(true)
    });
  }

  onCrashRecoveryChange(): void {
    this.jobService.setProjectCrashRecovery(this.projectName(), this.crashRecoveryDraft).subscribe({
      next: () => this.refreshAll(true),
      error: () => this.refreshAll(true)
    });
  }

  setAutoPushStrategy(strategy: AutoPushStrategy): void {
    if (this.autoPushStrategyDraft === strategy) return;
    this.autoPushStrategyDraft = strategy;
    this.jobService.setProjectAutoPushStrategy(this.projectName(), strategy).subscribe({
      next: () => this.refreshAll(true),
      error: () => this.refreshAll(true)
    });
  }

  /**
   * AGT-2839: writes the integration-gate reuse override. "Inherit" clears it
   * so the project falls back to on-where-Remote-Review-runs.
   */
  onIntegrationGateReuseChange(): void {
    const enabled =
      this.integrationGateReuseDraft === 'inherit'
        ? null
        : this.integrationGateReuseDraft === 'enabled';
    this.jobService.setProjectIntegrationGateReviewReuse(this.projectName(), enabled).subscribe({
      next: () => this.refreshAll(true),
      error: () => this.refreshAll(true),
    });
  }

  onOrchModelChange(): void {
    const model = this.orchModelDraft.trim();
    this.jobService.setProjectOrchestratorModel(this.projectName(), model || null).subscribe({
      next: () => this.refreshAll(true),
      error: () => this.refreshAll(true)
    });
  }

  /**
   * Read the resolved per-CLI permission modes for this project. Fills the
   * dropdown drafts from the effective mode so an un-overridden CLI shows its
   * global/default posture rather than a blank control. Runs on panel open
   * and after each write.
   */
  private refreshCliModes(): void {
    this.jobService.getProjectCliModes(this.projectName()).subscribe({
      next: (res) => {
        this.cliModeResolved.set(res.resolved ?? {});
        if (res.available?.length) this.cliModeAvailable.set(res.available);
        for (const cli of this.cliTypes) {
          const resolved = res.resolved?.[cli]?.mode ?? 'yolo';
          if (this.cliModeDraft[cli] !== resolved) this.cliModeDraft[cli] = resolved;
        }
      },
      error: () => { /* keep last known modes */ }
    });
  }

  /**
   * Persist one CLI's permission mode as a project override, then refresh the
   * resolved map. Takes effect on the next spawn without a backend restart.
   */
  onCliModeChange(cli: string): void {
    const mode = this.cliModeDraft[cli] ?? 'yolo';
    this.cliModeBusy[cli] = true;
    this.jobService.setProjectCliMode(this.projectName(), cli, mode).subscribe({
      next: () => { this.cliModeBusy[cli] = false; this.refreshCliModes(); },
      error: () => { this.cliModeBusy[cli] = false; this.refreshCliModes(); }
    });
  }

  /** Clear a CLI's project override, reverting to global config / platform default. */
  resetCliMode(cli: string): void {
    this.cliModeBusy[cli] = true;
    this.jobService.setProjectCliMode(this.projectName(), cli, '').subscribe({
      next: () => { this.cliModeBusy[cli] = false; this.refreshCliModes(); },
      error: () => { this.cliModeBusy[cli] = false; this.refreshCliModes(); }
    });
  }

  /**
   * T1b / ASS-1742: read the resolved per-CLI context modes for this project.
   * Fills the dropdown drafts from the effective mode (default CLEAN) so an
   * un-overridden CLI shows its platform posture rather than a blank control.
   */
  private refreshCliContextModes(): void {
    this.jobService.getProjectCliContextModes(this.projectName()).subscribe({
      next: (res) => {
        this.cliContextModeResolved.set(res.resolved ?? {});
        if (res.available?.length) this.cliContextModeAvailable.set(res.available);
        for (const cli of this.cliTypes) {
          const resolved = res.resolved?.[cli]?.mode ?? 'clean';
          if (this.cliContextModeDraft[cli] !== resolved) this.cliContextModeDraft[cli] = resolved;
        }
      },
      error: () => { /* keep last known modes */ }
    });
  }

  /**
   * Persist one CLI's context mode as a project override, then refresh the
   * resolved map. Takes effect on the next spawn without a backend restart.
   */
  onCliContextModeChange(cli: string): void {
    const mode = this.cliContextModeDraft[cli] ?? 'clean';
    this.cliContextModeBusy[cli] = true;
    this.jobService.setProjectCliContextMode(this.projectName(), cli, mode).subscribe({
      next: () => { this.cliContextModeBusy[cli] = false; this.refreshCliContextModes(); },
      error: () => { this.cliContextModeBusy[cli] = false; this.refreshCliContextModes(); }
    });
  }

  /** Clear a CLI's context-mode project override, reverting to the platform default (CLEAN). */
  resetCliContextMode(cli: string): void {
    this.cliContextModeBusy[cli] = true;
    this.jobService.setProjectCliContextMode(this.projectName(), cli, '').subscribe({
      next: () => { this.cliContextModeBusy[cli] = false; this.refreshCliContextModes(); },
      error: () => { this.cliContextModeBusy[cli] = false; this.refreshCliContextModes(); }
    });
  }

  repairQueueHealth(): void {
    if (this.queueRepairBusy()) return;
    this.queueRepairBusy.set(true);
    this.queueRepairMessage.set(null);
    this.jobService.repairProjectQueueHealth(this.projectName()).subscribe({
      next: (res) => {
        this.queueRepairBusy.set(false);
        this.queueHealth.set(res.queueHealth);
        this.queueRepairMessage.set(`Archived ${res.moved.length} folder${res.moved.length === 1 ? '' : 's'}.`);
        this.refreshAll(true);
      },
      error: (err) => {
        this.queueRepairBusy.set(false);
        this.queueRepairMessage.set(err?.error?.error || err?.message || 'Repair failed.');
      }
    });
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString();
    } catch {
      return iso;
    }
  }
}
