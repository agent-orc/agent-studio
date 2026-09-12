import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import {
  ProjectShellComponent,
  ProjectDetailComponent,
  ProjectOverviewDashboardComponent,
  ProjectDeploymentPanelComponent,
  ProjectSettingsPanelComponent,
  SecurityPanelComponent,
  UxuiPanelComponent,
  ProjectObservabilityPanelComponent,
  ProjectPipelinePanelComponent,
  ProjectSteeringDocsSectionComponent,
  ProjectWikiSectionComponent,
  ProjectUrlsPanelComponent,
  ProjectGitPanelComponent,
  ProjectIntegrationPanelComponent,
  ProjectProposalsPanelComponent,
  ProjectGraphComponent,
  ProjectTestRunsPanelComponent,
  OwnershipMappingPanelComponent,
} from '../../../project-detail';
import { ProjectTokenUsagePanelComponent } from '../../../project-token-usage';
import { ProjectCycleTimePanelComponent } from '../../../project-cycle-time';
import { PromptAdminPanelComponent } from '../../../orchestrator';
import { WorkspaceScreenshotsComponent } from '../../../screenshots';
import { RegressionRadarComponent } from '../../../regression-radar';
import {
  DEFAULT_PROJECT_RAIL_KEY,
  ProjectRailKey,
  isProjectRailKey,
} from '../../../project-detail/components/project-shell/project-shell.config';
import { ProjectOverlaysService } from '../../../project-detail/state/project-overlays.service';
import { StudioTabStateService } from '../../services/studio-tab-state.service';
import type { WikiTabNavigationRequest, WikiTabTarget } from '../../studio-shell.types';
import type { WorkbenchListItem } from '../../../../models/project-docs.model';

/** Rails whose content panel is real (not the project-shell placeholder). */
const RAILS_WITH_CUSTOM_PANEL: ReadonlySet<ProjectRailKey> = new Set<ProjectRailKey>([
  'overview',
  'deployment',
  'project-urls',
  'git',
  'integration',
  'visual-evidence',
  'security',
  'proposals',
  'architecture',
  'project-graph',
  'drift',
  'uxui',
  'test-quality',
  'token-usage',
  'cycle-time',
  'observability',
  'steering',
  'wiki',
  // Nav-rebuild step 2 (T5b): Pipeline / Workflow host real content moved
  // out of Project Settings, and Prompts keeps the application-wide
  // prompt-admin surface from the Context segment.
  'pipeline',
  'workflow',
  'prompts',
  'settings',
  // Settings tree sub-pages render the same real settings panel.
  'settings-defaults',
  'settings-overrides',
  'orchestrator',
  'ownership-routing',
]);

/**
 * Deck tab. The per-project landing surface inside the studio
 * editor. Embeds the legacy <app-project-shell> directly so the full
 * project navigation is reachable from the tab — no separate overlay —
 * and every rail uses its real content panel where one exists.
 *
 * The rail is a collapsible-segment tree (ASS-1711): Insight / Quality /
 * Context / Config segments fold; Context contains Architecture / Wiki /
 * Agent Docs / Prompts, and Settings expands to Workspace Defaults / Project
 * Overrides.
 */
@Component({
  selector: 'app-project-hub-view',
  standalone: true,
  imports: [
    ProjectShellComponent,
    ProjectDetailComponent,
    ProjectOverviewDashboardComponent,
    ProjectDeploymentPanelComponent,
    ProjectSettingsPanelComponent,
    SecurityPanelComponent,
    UxuiPanelComponent,
    ProjectTokenUsagePanelComponent,
    ProjectCycleTimePanelComponent,
    ProjectObservabilityPanelComponent,
    ProjectPipelinePanelComponent,
    ProjectSteeringDocsSectionComponent,
    ProjectWikiSectionComponent,
    ProjectUrlsPanelComponent,
    ProjectGitPanelComponent,
    ProjectIntegrationPanelComponent,
    ProjectProposalsPanelComponent,
    ProjectGraphComponent,
    ProjectTestRunsPanelComponent,
    OwnershipMappingPanelComponent,
    PromptAdminPanelComponent,
    WorkspaceScreenshotsComponent,
    RegressionRadarComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-hub-view.component.html',
  styleUrl: './project-hub-view.component.scss',
})
export class ProjectHubViewComponent {
  private readonly overlays = inject(ProjectOverlaysService);
  private readonly tabState = inject(StudioTabStateService);

  readonly projectName = input.required<string>();
  /** Immutable registry identity used when child views serialize shareable URLs. */
  readonly projectId = input<string | null>(null);
  /** Optional initial rail; defaults to "overview" if absent or unknown. */
  readonly initialSection = input<string>('overview');
  /** Exact Wiki destination owned by this shell tab. */
  readonly wikiTarget = input<WikiTabTarget | undefined>();
  /** Exact Project Pipeline row requested by a task-detail activation link. */
  readonly pipelineStepId = input<string | undefined>();

  /** Bubbles to the parent so it can navigate when a row link is clicked. */
  readonly openTask = output<{ jobId: string; watchPath: string }>();

  readonly activeRail = signal<ProjectRailKey>(DEFAULT_PROJECT_RAIL_KEY);

  constructor() {
    effect(() => {
      const raw = this.initialSection();
      this.activeRail.set(isProjectRailKey(raw) ? raw : DEFAULT_PROJECT_RAIL_KEY);
    });
  }

  hasCustomPanel(rail: ProjectRailKey): boolean {
    if (rail === 'project-graph') return true;
    return RAILS_WITH_CUSTOM_PANEL.has(rail);
  }

  /** Settings and its tree sub-pages all render the one real settings panel. */
  isSettingsRail(rail: ProjectRailKey): boolean {
    return rail === 'settings' || rail === 'settings-defaults' || rail === 'settings-overrides';
  }

  setRail(rail: ProjectRailKey): void {
    const projectName = this.projectName();
    this.activeRail.set(rail);
    this.tabState.open({
      kind: 'hub',
      projectName,
      section: rail,
      ...(rail === 'wiki' ? { wikiTarget: { kind: 'overview' } as const } : {}),
    });
  }

  openWikiTarget(request: WikiTabNavigationRequest): void {
    this.tabState.open({
      kind: 'hub',
      projectName: this.projectName(),
      section: 'wiki',
      wikiTarget: request.target,
    }, request.reuse);
  }

  /**
   * Open the orchestrator feed overlay (same target as the legacy
   * project-shell feed button).
   */
  openFeed(): void {
    this.overlays.openOrchFeed(this.projectName());
  }

  openFeedFromDetail(intent: string): void {
    void intent;
    this.overlays.openOrchFeed(this.projectName());
  }

  openReport(report: { reportId: string }): void {
    this.overlays.openAnalysisReport(this.projectName(), report.reportId);
  }

  /**
   * AGT-2067 — open a configured URL as an embedded preview tab from the
   * Project URLs management page. Same `url-preview` tab the Explorer row
   * opens (open-or-focus by key), so both entry points land on one tab.
   */
  openUrlPreview(url: { id: string }): void {
    this.tabState.open({ kind: 'url-preview', projectName: this.projectName(), urlId: url.id }, 'new');
  }

  openWorkbench(workbench: WorkbenchListItem): void {
    if (!workbench.valid) return;
    this.tabState.open({
      kind: 'workbench',
      projectName: this.projectName(),
      workbenchId: workbench.id,
      title: workbench.title,
      key: workbench.key ?? undefined,
    }, 'replace-current');
  }

  /** Deck closes when the user closes the editor tab; the in-rail button only collapses navigation. */
  closeShell(): void {
    /* legacy output hook: ProjectShell owns navigation collapse internally */
  }

  onSecurityFollowUp(evt: unknown): void { void evt; }
  onSecurityOpenEvidence(evt: unknown): void { void evt; }
  onSecurityAuditQueued(evt: unknown): void { void evt; }
  onUxuiFollowUp(evt: unknown): void { void evt; }
  onUxuiActionQueued(evt: unknown): void { void evt; }
}
