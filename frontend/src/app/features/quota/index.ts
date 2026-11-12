/** Quota feature public API. Cycle 9h / ADR-0034. */
export { QuotaApiService } from './services/quota-api.service';
export type { CliModelRouteProfile, CliModelFallbackRoute, CliFallbackState, CliModelRoutesResponse, CliQuotaWaitPolicy, ProjectCliQuotaWaitPolicy, ModelRoutingRecommendation, ModelRoutingPolicyView,
  EffectiveModelSelection, ConsumerModelPolicy, ConsumerPolicyRoute, ModelSelectionPolicy } from './services/quota-api.service';
// Forecast panel, curve and workspace disclosure load through ./lazy.
export { TaskCardQuotaNoticeComponent } from './components/task-card-quota-notice/task-card-quota-notice';
export { WorkspaceQuotaBannerComponent } from './components/workspace-quota-banner/workspace-quota-banner';
export { WorkspaceQuotaBoardState, type WorkspaceQuotaAdmission, type WorkspaceQuotaBoardAdmission, type WorkspaceQuotaStatus } from './state/workspace-quota-board-state.service';
export type { QuotaWindow, QuotaSnapshot, QuotaReport } from './models/quota.model';
export type { QuotaHistoryResponse, QuotaHistoryWindow, QuotaHistoryPoint, QuotaForecast, QuotaForecastStatus, QuotaWindowKind } from './models/quota-history.model';
export { quotaFallbackStatus, type QuotaFallbackStatus } from './quota-forecast.util';
export { quotaProbeFailureLabel, quotaSnapshotIsStale } from './quota-freshness.util';
