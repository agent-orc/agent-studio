import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ProjectDetailComponent } from '../project-detail/project-detail';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { ClientDefaultsService } from '../../../../services/client-defaults.service';
import { QuotaApiService, type ProjectCliQuotaWaitPolicy } from '../../../../features/quota';
// Service-only direct import avoids evaluating the heavy shell component barrel,
// which closes a runtime cycle when Project Settings mounts in Project Hub.
import { WorkspaceOverlaysService } from '../../../shell/state/workspace-overlays.service';
import type { CliType } from '../../../../models/task.model';
import { CLI_TYPES } from '../../../../models/task.model';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';
import { CliCatalogStore } from '../../../cli';
import {
  WorkspaceOrchestratorSettingsService,
  type WorkspaceOrchestratorSettings,
} from '../../../../services/workspace-orchestrator-settings.service';
import { ExecutionAssignmentCardComponent } from '../execution-assignment-card/execution-assignment-card';
import { ParallelExecutionCardComponent } from '../parallel-execution-card/parallel-execution-card';
import { ProjectBasicsCardComponent } from '../project-basics-card/project-basics-card.component';
import { ProjectUrlsPanelComponent } from '../project-urls-panel/project-urls-panel.component';
import {
  ProjectBuildProfileNoticeComponent,
  type BuildProfileGateSummary,
} from '../project-build-profile-notice/project-build-profile-notice.component';
import { RetentionRulesTableComponent } from '../../../retention/components/retention-rules-table/retention-rules-table.component';
import { ProjectExecutionDefinitionComponent } from '../project-execution-definition/project-execution-definition';

const STORAGE_DEFAULT_CLI = 'defaultCliType';
const STORAGE_DEFAULT_MODEL_PREFIX = 'defaultModel:';
const STORAGE_DEFAULT_THINKING_PREFIX = 'defaultThinkingLevel:';

interface CapSummaryRow {
  cliType: CliType;
  label: string;
  icon: string;
  windows: { windowLabel: string; capPct: number }[];
}

/** ADR-0026 autonomy stops, shared with the per-project slider labels. */
const AUTONOMY_STOPS: readonly { level: number; name: string }[] = [
  { level: 0, name: 'Manual' },
  { level: 1, name: 'Cautious' },
  { level: 2, name: 'Balanced' },
  { level: 3, name: 'Confident' },
  { level: 4, name: 'Fully auto' },
];

interface ProjectSummaryLite {
  id: string;
  displayName: string;
  workspaceId: string;
  wikiSourceBranch?: string | null;
}

interface WorkspaceListItemLite {
  id: string;
  displayName: string;
  projects?: ProjectSummaryLite[];
}

/**
 * Project-level Settings panel. Mirrors the global Workspace-settings home
 * ("Dach"): a header + a "Workspace defaults" card section that surfaces the
 * global default agent (CLI + model) and the per-CLI usage caps, each labelled
 * as inherited from the global Workspace settings.
 *
 * They render read-only with a deep-link affordance into the matching global
 * Workspace-settings section (`overview` for the default-agent fallback,
 * `caps` for usage caps). Project basics owns the editable per-project coding
 * agent override. Runner mode, orchestrator model, auto-commit, and auto-push
 * keep living in the project-specific controls below.
 *
 * Nav-rebuild step 2 (T5b) relocated three formerly-embedded sections to their
 * own project rails — lane sort → Workflow, pipeline steps → Pipeline, CLI
 * permission modes → workspace Admin / CLI & Modelle — so Settings shrinks to
 * the rest. The controls are unchanged (Funktions-Diff = 0); only the mount
 * location moved.
 */
@Component({
  selector: 'app-project-settings-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ProjectDetailComponent,
    CliModelSelectorComponent,
    TooltipDirective,
    ExecutionAssignmentCardComponent,
    ParallelExecutionCardComponent,
    ProjectBasicsCardComponent,
    ProjectUrlsPanelComponent,
    ProjectBuildProfileNoticeComponent,
    RetentionRulesTableComponent,
    ProjectExecutionDefinitionComponent,
  ],
  templateUrl: './project-settings-panel.component.html',
  styleUrl: './project-settings-panel.component.scss',
})
export class ProjectSettingsPanelComponent implements OnInit {
  readonly projectName = input.required<string>();

  private readonly clientDefaults = inject(ClientDefaultsService);
  private readonly quotaApi = inject(QuotaApiService);
  private readonly overlays = inject(WorkspaceOverlaysService);
  private readonly http = inject(HttpClient);
  private readonly cliCatalog = inject(CliCatalogStore);
  private readonly workspaceOrchestrator = inject(WorkspaceOrchestratorSettingsService);

  // Per-project max parallelism lives in ParallelExecutionCardComponent: it is
  // deprecated for remote execution (host capacity is the source) but still
  // governs local runs, so the card renders only for local projects.
  readonly projectId = signal<string | null>(null);
  readonly wikiSourceBranch = signal('');
  readonly wikiSourceSaving = signal(false);
  readonly wikiSourceError = signal<string | null>(null);
  readonly wikiBranchOptions = signal<string[]>([]);
  readonly projectQuotaWait = signal<ProjectCliQuotaWaitPolicy | null>(null);
  readonly quotaWaitSaving = signal(false);
  readonly buildProfileGate = signal<BuildProfileGateSummary | null>(null);

  // --- AGT-1812: editable workspace-default orchestrator settings ---------
  /** The workspace that owns this project; the tier these defaults write to. */
  readonly workspaceId = signal<string | null>(null);
  readonly workspaceName = signal<string>('');
  readonly orchestratorSettings = signal<WorkspaceOrchestratorSettings | null>(null);
  readonly autonomyStops = AUTONOMY_STOPS;
  readonly orchSaving = signal<boolean>(false);

  /** Claude model options for the orchestrator model select ('' = workspace has no default). */
  readonly orchModelOptions = computed(() => [
    { id: '', label: 'Platform default' },
    ...this.cliCatalog
      .modelsFor('claude')
      .filter((m) => m.available !== false)
      .map((m) => ({ id: m.id, label: m.isDefault ? `Default (${m.label})` : m.label })),
  ]);

  /** The stored workspace-default model, or '' when none is set. */
  readonly orchModel = computed(() => this.orchestratorSettings()?.orchestratorModel ?? '');
  /** The stored workspace-default autonomy, or -1 ("Inherit") when none is set. */
  readonly orchAutonomy = computed(() => this.orchestratorSettings()?.autonomyLevel ?? -1);

  /** Global default-agent fallback, read-only; Project basics may override it. */
  readonly defaultCli = signal<CliType | null>(null);
  readonly defaultModel = signal<string | null>(null);
  readonly defaultThinkingLevel = signal<string | null>(null);

  /** Global per-CLI usage caps, read-only. */
  readonly defaultCapPct = signal<number>(95);
  private readonly caps = signal<Record<string, Record<string, number>>>({});

  readonly capRows = computed<CapSummaryRow[]>(() => {
    const caps = this.caps();
    const out: CapSummaryRow[] = [];
    for (const cli of Object.keys(caps) as CliType[]) {
      const windows = Object.entries(caps[cli] ?? {})
        .filter(([label]) => !!label)
        .map(([windowLabel, capPct]) => ({ windowLabel, capPct }));
      if (windows.length === 0) continue;
      out.push({
        cliType: cli,
        label: cliTypeLabel(cli),
        icon: cliTypeIcon(cli),
        windows,
      });
    }
    return out;
  });

  ngOnInit(): void {
    // Seed from the localStorage cache the status bar writes so the chip
    // shows a sensible value immediately, then reconcile with the durable
    // backend value (the one the orchestrator actually inherits).
    this.defaultCli.set(this.readStoredCli());
    this.defaultModel.set(this.readStoredModel(this.readStoredCli()));
    this.defaultThinkingLevel.set(this.readStoredThinkingLevel(this.readStoredCli()));
    this.clientDefaults.getDefaults().subscribe({
      next: (r) => {
        const cli = r?.defaultCliType;
        if (cli && (CLI_TYPES as string[]).includes(cli)) {
          this.defaultCli.set(cli as CliType);
          this.defaultModel.set(r?.defaultModel ?? this.readStoredModel(cli as CliType));
          this.defaultThinkingLevel.set(r?.defaultThinkingLevel ?? this.readStoredThinkingLevel(cli as CliType));
        }
      },
      error: () => { /* keep the localStorage-seeded value */ },
    });
    this.quotaApi.getQuotaCaps().subscribe({
      next: (resp) => {
        this.defaultCapPct.set(resp.defaultCapPct);
        this.caps.set(resp.caps ?? {});
      },
      error: () => { /* keep the default-cap fallback */ },
    });
    this.loadWorkspaceOrchestratorSettings();
    this.http.get<BuildProfileGateSummary>(
      `/api/projects/${encodeURIComponent(this.projectName())}/build-profile`,
    ).subscribe({
      next: summary => this.buildProfileGate.set(summary),
      error: () => { /* Do not infer an empty gate when project discovery failed. */ },
    });
    this.quotaApi.getProjectQuotaWaitPolicy(this.projectName()).subscribe({
      next: policy => this.projectQuotaWait.set(policy),
      error: () => { /* card keeps its safe inherited fallback */ },
    });
  }

  /**
   * AGT-1812: resolve this project's owning workspace, then load its default
   * orchestrator settings so the "Orchestrator" card can edit the workspace
   * tier (project overrides still win, and live in the Project overrides
   * section below).
   */
  private loadWorkspaceOrchestratorSettings(): void {
    this.http
      .get<WorkspaceListItemLite[]>('/api/workspaces?includeArchived=true')
      .subscribe({
        next: (workspaces) => {
          const name = this.projectName();
          const owner = (workspaces ?? []).find((w) =>
            (w.projects ?? []).some(
              (p) => p.displayName === name || p.id === name,
            ),
          );
          if (!owner) return; // project not mapped to a registry workspace yet
          const project = (owner.projects ?? []).find((p) => p.displayName === name || p.id === name);
          this.projectId.set(project?.id ?? null);
          this.wikiSourceBranch.set(project?.wikiSourceBranch ?? '');
          this.loadWikiBranches();
          this.workspaceId.set(owner.id);
          this.workspaceName.set(owner.displayName);
          this.workspaceOrchestrator.get(owner.id).subscribe({
            next: (s) => this.orchestratorSettings.set(s),
            error: () => { /* card falls back to platform-default labels */ },
          });
        },
        error: () => { /* no workspace context; card stays hidden */ },
      });
  }

  private loadWikiBranches(): void {
    this.http.get<{ branches?: { name: string; upstream?: string | null }[] }>(
      `/api/git/inventory?project=${encodeURIComponent(this.projectName())}`,
    ).subscribe({
      next: inventory => {
        const values = new Set<string>();
        for (const branch of inventory.branches ?? []) {
          if (branch.name) values.add(branch.name);
          if (branch.upstream) values.add(branch.upstream);
        }
        if (this.wikiSourceBranch()) values.add(this.wikiSourceBranch());
        this.wikiBranchOptions.set([...values].sort((a, b) => a.localeCompare(b)));
      },
      error: () => { /* Checkout remains available without branch inventory. */ },
    });
  }

  setWikiSourceBranch(branch: string): void {
    const id = this.projectId();
    if (!id) return;
    const selected = branch.trim();
    this.wikiSourceSaving.set(true);
    this.wikiSourceError.set(null);
    this.http.put<{ wikiSourceBranch?: string | null }>(`/api/projects/${encodeURIComponent(id)}`, {
      wikiSourceBranch: selected || undefined,
      clearWikiSourceBranch: selected.length === 0,
    }).subscribe({
      next: project => {
        this.wikiSourceBranch.set(project.wikiSourceBranch ?? '');
        this.wikiSourceSaving.set(false);
      },
      error: err => {
        this.wikiSourceSaving.set(false);
        this.wikiSourceError.set(err?.error?.error ?? 'Could not update the wiki source.');
      },
    });
  }

  /** Persist the workspace-default orchestrator model (blank clears it). */
  onWorkspaceModelChange(model: string): void {
    const id = this.workspaceId();
    if (!id) return;
    this.orchSaving.set(true);
    this.workspaceOrchestrator.setModel(id, model || null).subscribe({
      next: (r) => {
        this.orchSaving.set(false);
        this.orchestratorSettings.update((s) =>
          s ? { ...s, orchestratorModel: r.orchestratorModel, orchestratorThinkingLevel: r.orchestratorThinkingLevel } : s,
        );
      },
      error: () => this.orchSaving.set(false),
    });
  }

  /** Persist the workspace-default autonomy level; -1 clears it (inherit platform default). */
  onWorkspaceAutonomyChange(level: number): void {
    const id = this.workspaceId();
    if (!id) return;
    const value = level < 0 ? null : level;
    this.orchSaving.set(true);
    this.workspaceOrchestrator.setAutonomy(id, value).subscribe({
      next: (r) => {
        this.orchSaving.set(false);
        this.orchestratorSettings.update((s) => (s ? { ...s, autonomyLevel: r.autonomyLevel } : s));
      },
      error: () => this.orchSaving.set(false),
    });
  }

  quotaWaitOverride(): 'inherit' | 'enabled' | 'disabled' {
    const value = this.projectQuotaWait()?.projectEnabled;
    return value === null || value === undefined ? 'inherit' : value ? 'enabled' : 'disabled';
  }

  setProjectQuotaWaitOverride(value: string): void {
    const enabled = value === 'inherit' ? null : value === 'enabled';
    const threshold = value === 'inherit'
      ? null
      : (this.projectQuotaWait()?.projectThresholdMinutes ?? this.projectQuotaWait()?.thresholdMinutes ?? 30);
    this.saveProjectQuotaWait(enabled, threshold);
  }

  setProjectQuotaWaitThreshold(value: number): void {
    const enabled = this.quotaWaitOverride() === 'disabled' ? false : true;
    this.saveProjectQuotaWait(enabled, Math.max(1, Math.min(240, Math.round(Number(value) || 30))));
  }

  private saveProjectQuotaWait(enabled: boolean | null, thresholdMinutes: number | null): void {
    this.quotaWaitSaving.set(true);
    this.quotaApi.setProjectQuotaWaitPolicy(this.projectName(), enabled, thresholdMinutes).subscribe({
      next: policy => {
        this.projectQuotaWait.set(policy);
        this.quotaWaitSaving.set(false);
      },
      error: () => this.quotaWaitSaving.set(false),
    });
  }

  private readStoredCli(): CliType | null {
    const stored = localStorage.getItem(STORAGE_DEFAULT_CLI);
    return stored && (CLI_TYPES as string[]).includes(stored) ? (stored as CliType) : null;
  }

  private readStoredModel(cli: CliType | null): string | null {
    if (!cli) return null;
    return localStorage.getItem(STORAGE_DEFAULT_MODEL_PREFIX + cli);
  }

  private readStoredThinkingLevel(cli: CliType | null): string | null {
    if (!cli) return null;
    return localStorage.getItem(STORAGE_DEFAULT_THINKING_PREFIX + cli);
  }

  /** Open the global Workspace-settings home (where the default agent lives). */
  openWorkspaceSettings(): void {
    this.overlays.openSettings();
  }

  /** Open the global Workspace-settings usage-caps section directly. */
  openUsageCaps(): void {
    this.overlays.openCliAdmin();
  }
}
