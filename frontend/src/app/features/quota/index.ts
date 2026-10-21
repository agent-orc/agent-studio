/** Quota feature public API. Cycle 9h / ADR-0034. */
export { QuotaApiService } from './services/quota-api.service';
export type { CliModelRouteProfile, CliModelFallbackRoute, CliFallbackState, CliModelRoutesResponse, CliQuotaWaitPolicy, ProjectCliQuotaWaitPolicy, ModelRoutingRecommendation, ModelRoutingPolicyView } from './services/quota-api.service';
export { QuotaStripComponent } from './components/quota-strip/quota-strip';
export { HeaderQuotaComponent } from './components/header-quota/header-quota';
export { QuotaForecastPanelComponent } from './components/quota-forecast-panel/quota-forecast-panel';
export { QuotaCurveComponent } from './components/quota-curve/quota-curve';
export { TaskCardQuotaNoticeComponent } from './components/task-card-quota-notice/task-card-quota-notice';
export { WorkspaceQuotaBoardState, type WorkspaceQuotaAdmission, type WorkspaceQuotaBoardAdmission, type WorkspaceQuotaStatus } from './state/workspace-quota-board-state.service';
export type { QuotaWindow, QuotaSnapshot, QuotaReport } from './models/quota.model';
export type { QuotaHistoryResponse, QuotaHistoryWindow, QuotaHistoryPoint, QuotaForecast, QuotaForecastStatus, QuotaWindowKind } from './models/quota-history.model';
export { quotaFallbackStatus, type QuotaFallbackStatus } from './quota-forecast.util';
export { quotaProbeFailureLabel, quotaSnapshotIsStale } from './quota-freshness.util';
