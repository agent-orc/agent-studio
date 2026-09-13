/** Project-detail feature public API. Cycle 9h / ADR-0034. */

// state
export { ProjectOverlaysService } from './state/project-overlays.service';
export {
  isProjectHubRoute,
  parseProjectHubRoute,
  projectHubRoute,
  withProjectHubRoute,
  type ProjectHubRouteProject,
  type ProjectHubRouteTarget,
} from './state/project-hub-route';

// container components
export { ProjectOverlaysComponent } from './components/project-overlays/project-overlays.component';
export { ProjectDetailComponent } from './components/project-detail/project-detail';
export { ProjectOverviewDashboardComponent } from './components/project-overview-dashboard/project-overview-dashboard';
export { ProjectDeploymentPanelComponent } from './components/project-deployment-panel/project-deployment-panel.component';
export { ProjectShellComponent } from './components/project-shell/project-shell.component';
export { ProjectSettingsPanelComponent } from './components/project-settings-panel/project-settings-panel.component';
export { ProjectExecutionDefinitionComponent } from './components/project-execution-definition/project-execution-definition';
export { AnalysisReportDrilldownComponent } from './components/analysis-report-drilldown/analysis-report-drilldown';
export { AutonomySliderComponent } from './components/autonomy-slider/autonomy-slider';

// rail panels (consumed by project-overlays + occasionally externally)
export { SecurityPanelComponent } from './components/security-panel/security-panel.component';
export { UxuiPanelComponent } from './components/uxui-panel/uxui-panel.component';
export { ProjectObservabilityPanelComponent } from './components/project-observability/project-observability-panel.component';
export { ProjectProductRuntimePanelComponent } from './components/project-product-runtime/project-product-runtime-panel.component';
export { ProjectPipelinePanelComponent } from './components/project-pipeline-panel/project-pipeline-panel.component';
export { ProjectTestRunsPanelComponent } from './components/project-test-runs-panel/project-test-runs-panel';
export { ProjectUrlsPanelComponent } from './components/project-urls-panel/project-urls-panel.component';
export { ProjectUrlPreviewTabComponent } from './components/project-url-preview-tab/project-url-preview-tab.component';
export { WorkbenchTabHostComponent } from './components/workbench-tab-host/workbench-tab-host.component';
export { WorkbenchDecisionPanelComponent } from './components/workbench-decision-panel/workbench-decision-panel';
export { WorkbenchDecisionStore } from './state/workbench-decision.store';
export { PageActionBarComponent } from './components/page-action-bar/page-action-bar';
export { ProjectGitPanelComponent } from './components/project-git-panel/project-git-panel.component';
export { ProjectIntegrationPanelComponent } from './components/project-integration-panel/project-integration-panel.component';
export { ProjectProposalsPanelComponent } from './components/project-proposals-panel/project-proposals-panel.component';
export { ProjectGraphComponent } from './components/project-graph/project-graph.component';
export { OwnershipMappingPanelComponent } from './components/ownership-mapping-panel/ownership-mapping-panel.component';

// section components used cross-feature
export { ProjectSteeringDocsSectionComponent } from './components/project-steering-docs-section/project-steering-docs-section';
export { ProjectSkillReadinessSectionComponent } from './components/project-skill-readiness-section/project-skill-readiness-section';
export { ProjectWikiSectionComponent } from './components/project-wiki-section/project-wiki-section';
export { ProjectWorkflowSectionComponent } from './components/project-workflow-section/project-workflow-section';

// project-shell config (deep-link slug helpers)
export {
  DEFAULT_PROJECT_RAIL_KEY,
  isProjectRailKey,
  projectRailLabel,
  toProjectSlug,
  type ProjectRailKey,
} from './components/project-shell/project-shell.config';

// types
export type {
  SecurityReviewSummary,
  SecurityBaselineResponse,
  SecurityReviewListResponse,
  SecurityAuditQueueResponse,
} from './components/security-panel/security-panel.types';
export type {
  AcceptCouncilNoteResponse,
  DesignActionKind,
  DesignActionQueueResponse,
  DesignCouncilResponse,
  DesignOverviewResponse,
  DesignReferencesResponse,
} from './components/uxui-panel/uxui-panel.types';
