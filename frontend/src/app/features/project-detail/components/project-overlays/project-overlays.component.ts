import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, output } from '@angular/core';
import { ProjectOverlaysService } from '../../state/project-overlays.service';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { OrchestratorFeedComponent, PromptAdminPanelComponent } from '../../../orchestrator';
import { ProjectDetailComponent } from '../project-detail/project-detail';
import { ProjectOverviewDashboardComponent } from '../project-overview-dashboard/project-overview-dashboard';
import { ProjectDeployAreaComponent } from '../project-deploy-area/project-deploy-area';
import { ProjectUrlsPanelComponent } from '../project-urls-panel/project-urls-panel.component';
import { ProjectGraphComponent } from '../project-graph/project-graph.component';
import { ProjectSettingsPanelComponent } from '../project-settings-panel/project-settings-panel.component';
import { ProjectShellComponent } from '../project-shell/project-shell.component';
import { SecurityPanelComponent } from '../security-panel/security-panel.component';
import { UxuiPanelComponent } from '../uxui-panel/uxui-panel.component';
import { ProjectTokenUsagePanelComponent } from '../../../project-token-usage';
import { ProjectObservabilityPanelComponent } from '../project-observability/project-observability-panel.component';
import { ProjectEnvironmentOverviewComponent } from '../project-environment-overview/project-environment-overview.component';
import { ProjectProductRuntimePanelComponent } from '../project-product-runtime/project-product-runtime-panel.component';
import { ProjectTestRunsPanelComponent } from '../project-test-runs-panel/project-test-runs-panel';
import { ProjectSteeringDocsSectionComponent } from '../project-steering-docs-section/project-steering-docs-section';
import { ProjectWikiSectionComponent } from '../project-wiki-section/project-wiki-section';
import { AnalysisReportDrilldownComponent } from '../analysis-report-drilldown/analysis-report-drilldown';
import { ProjectRailKey, resolveProjectSection } from '../project-shell/project-shell.config';
import { WorkspaceScreenshotsComponent } from '../../../screenshots';
import type { TaskScreenshot } from '../../../screenshots';
import { RegressionRadarComponent } from '../../../regression-radar';
import { OverlayPortalDirective } from '../../../../directives/overlay-portal.directive';

import { TooltipDirective } from 'coding-agent-chat/shared';
/**
 * Cycle 9g project-detail-feature container: renders the project-level
 * overlays (orch-feed / project-shell / analysis-report). The former
 * project-detail overlay is mounted inside the project-shell settings
 * rail so settings keep their functionality without a second window.
 * Open/close + URL-hash sync owned by
 * ProjectOverlaysService; the shell only mounts this component once.
 *
 * Per-rail follow-up events bubble up because they trigger
 * create-job-dialog, whose form state lives in the shell.
 */
@Component({
  selector: 'app-project-overlays',
  standalone: true,
  imports: [
    OrchestratorFeedComponent,
    ProjectDetailComponent,
    ProjectOverviewDashboardComponent,
    ProjectDeployAreaComponent,
    ProjectUrlsPanelComponent,
    ProjectGraphComponent,
    ProjectSettingsPanelComponent,
    ProjectShellComponent,
    SecurityPanelComponent,
    UxuiPanelComponent,
    ProjectTokenUsagePanelComponent,
    ProjectObservabilityPanelComponent,
    ProjectEnvironmentOverviewComponent,
    ProjectProductRuntimePanelComponent,
    ProjectTestRunsPanelComponent,
    ProjectSteeringDocsSectionComponent,
    ProjectWikiSectionComponent,
    AnalysisReportDrilldownComponent,
    WorkspaceScreenshotsComponent,
    RegressionRadarComponent,
    PromptAdminPanelComponent,
    TooltipDirective,
    OverlayPortalDirective
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-overlays.component.html',
})
export class ProjectOverlaysComponent {
  readonly overlays = inject(ProjectOverlaysService);

  // Per-rail follow-up events bubble up to the shell because they
  // trigger create-job-dialog (form state lives there).
  readonly securityFollowUp = output<{ projectName: string; prefill: string }>();
  readonly securityOpenEvidence = output<{ projectName: string; relPath: string }>();
  readonly securityAuditQueued = output<{ projectName: string; jobId: string }>();
  readonly uxuiFollowUp = output<{ projectName: string; prefill: string; title: string }>();
  readonly uxuiActionQueued = output<{ projectName: string; action: string; jobId: string }>();
  readonly openTask = output<Pick<TaskScreenshot, 'jobId' | 'watchPath'>>();

  /** The shell needs to provide watchPaths for hash → name resolution on rail change. */
  readonly railChangeNeedsWatchPaths = output<ProjectRailKey>();

  /** Retired Deck sections (`pipeline`, `observability`) open the Deploy and Operations areas. */
  readonly section = computed(() => resolveProjectSection(this.overlays.projectShellRail()));

  hasCustomPanel(rail: ProjectRailKey): boolean {
    const resolved = resolveProjectSection(rail).rail;
    if (resolved === 'deploy' || resolved === 'operations') return true;
    return rail === 'overview'
      || rail === 'project-urls'
      || rail === 'security'
      || rail === 'visual-evidence'
      || rail === 'architecture'
      || rail === 'project-graph'
      || rail === 'drift'
      || rail === 'uxui'
      || rail === 'test-quality'
      || rail === 'token-usage'
      || rail === 'agent-activity'
      || rail === 'steering'
      || rail === 'wiki'
      || rail === 'settings'
      // Nav-rebuild step 2 (T5b): workflow / prompts host the real content
      // relocated out of Project Settings + the prompt-admin surface, so they
      // render their own panel (not the shell placeholder).
      || rail === 'workflow'
      || rail === 'prompts'
      || rail === 'orchestrator';
  }

  setProjectShellRail(key: ProjectRailKey): void {
    this.overlays.setProjectShellRail(key);
  }

  openTaskFromProjectShell(task: Pick<TaskScreenshot, 'jobId' | 'watchPath'>): void {
    this.overlays.closeProjectShell();
    this.openTask.emit(task);
  }

  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);
  private disposers = new Map<string, () => void>();

  constructor() {
    // Each overlay layer registers itself when open. They sit above any
    // task detail / Add Task that is already on the stack, so Escape
    // closes them first. Analysis-report stacks above the project-shell
    // when both are visible because it pushes later.
    this.bindBool('project-shell-overlay', () => this.overlays.projectShellName() !== null, () => this.overlays.closeProjectShell());
    this.bindBool('orch-feed-overlay', () => this.overlays.orchFeedProject() !== null, () => this.overlays.closeOrchFeed());
    this.bindBool('analysis-report-overlay', () => this.overlays.analysisReportFocus() !== null, () => this.overlays.closeAnalysisReport());
    this.destroyRef.onDestroy(() => {
      for (const d of this.disposers.values()) d();
      this.disposers.clear();
    });
  }

  private bindBool(id: string, open: () => boolean, close: () => void): void {
    effect(() => {
      const isOpen = open();
      const existing = this.disposers.get(id);
      if (isOpen && !existing) {
        this.disposers.set(id, this.modalStack.push(id, close));
      } else if (!isOpen && existing) {
        existing();
        this.disposers.delete(id);
      }
    });
  }
}
