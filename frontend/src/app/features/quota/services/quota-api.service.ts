import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { QuotaReport, QuotaSnapshot } from '../models/quota.model';

export interface CliModelRouteProfile {
  cliType: string;
  primaryModel: string | null;
  primaryThinkingLevel: string | null;
  fallbackCliType: string | null;
  fallbackModel: string | null;
  fallbackThinkingLevel: string | null;
  /** True when the fallback fields came from the equivalence catalogue
   *  (AGT-2751) rather than an explicit operator save via `setModelRoute`. */
  isFallbackDerived?: boolean;
}

export interface CliQuotaWaitPolicy {
  enabled: boolean;
  thresholdMinutes: number;
}

export interface ProjectCliQuotaWaitPolicy extends CliQuotaWaitPolicy {
  source: 'project' | 'global';
  projectEnabled: boolean | null;
  projectThresholdMinutes: number | null;
  globalEnabled: boolean;
  globalThresholdMinutes: number;
}

/**
 * Cycle 10d API client for the per-CLI quota / rate-limit endpoints.
 * Lifted out of the TaskService god-service per ADR-0034 so the per-feature
 * HTTP surface is owned by the feature folder.
 *
 * Wraps `/api/cli/quota` (cached + force-refresh) and the per-CLI
 * usage caps endpoints under `/api/cli/quota/caps`.
 */
/** Policy-derived route suggestion for one task (band -> model x thinking-level). */
export interface ModelRoutingRecommendation {
  model: string;
  thinkingLevel: string | null;
  tier: string;
  taskType: string;
  economyDowngraded: boolean;
  policyVersion: string;
  policyWikiPath: string;
}

/** The active routing policy shown in the CLI-models panel. */
export interface ModelRoutingPolicyView {
  economyMode: boolean;
  policyVersion: string;
  rows: { tier: string; model: string; thinkingLevel: string | null }[];
}

@Injectable({ providedIn: 'root' })
export class QuotaApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api';

  /**
   * Cached snapshot of every CLI's quota windows. Returns immediately
   * with the on-disk snapshot; the backend re-probes stale entries
   * in the background. Use this for the strip + donut surfaces.
   */
  getQuotaReport() {
    return this.http.get<QuotaReport>(`${this.baseUrl}/cli/quota`);
  }

  /**
   * Force a synchronous re-probe of every CLI. Spawns fresh PTYs; takes
   * several seconds. Use this for the strip-level "refresh all" button.
   */
  refreshQuotaAll() {
    return this.http.post<QuotaReport>(`${this.baseUrl}/cli/quota/refresh`, {});
  }

  /**
   * Force a synchronous re-probe of one CLI's quota. Spawns a fresh
   * PTY; takes several seconds. Use this for the per-CLI "↻" button.
   */
  refreshQuotaForCli(cliType: string) {
    return this.http.post<QuotaSnapshot>(
      `${this.baseUrl}/cli/quota/refresh/${cliType}`,
      {},
    );
  }

  /**
   * Per-CLI usage-cap configuration (admin panel). The map shape is
   * `{ defaultCapPct, caps: { [cliType]: { [windowKey]: capPct } } }`.
   */
  getQuotaCaps() {
    return this.http.get<{
      defaultCapPct: number;
      caps: Record<string, Record<string, number>>;
    }>(`${this.baseUrl}/cli/quota/caps`);
  }

  /**
   * Update one cap. The runner blocks pickup and stops in-flight runs
   * when usage crosses these caps so the user keeps a buffer for
   * ad-hoc work outside the orchestrator.
   */
  setQuotaCap(cliType: string, windowLabel: string, capPct: number) {
    return this.http.put<{
      defaultCapPct: number;
      caps: Record<string, Record<string, number>>;
    }>(`${this.baseUrl}/cli/quota/caps`, { cliType, windowLabel, capPct });
  }

  getQuotaWaitPolicy() {
    return this.http.get<CliQuotaWaitPolicy>(`${this.baseUrl}/cli/quota/wait-policy`);
  }

  setQuotaWaitPolicy(enabled: boolean, thresholdMinutes: number) {
    return this.http.put<CliQuotaWaitPolicy>(`${this.baseUrl}/cli/quota/wait-policy`, {
      enabled,
      thresholdMinutes,
    });
  }

  getProjectQuotaWaitPolicy(projectName: string) {
    return this.http.get<ProjectCliQuotaWaitPolicy>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/quota-wait-policy`,
    );
  }

  setProjectQuotaWaitPolicy(projectName: string, enabled: boolean | null, thresholdMinutes: number | null) {
    return this.http.put<ProjectCliQuotaWaitPolicy>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/quota-wait-policy`,
      { enabled, thresholdMinutes },
    );
  }

  getModelRoutes() {
    return this.http.get<{ profiles: Record<string, CliModelRouteProfile> }>(
      `${this.baseUrl}/cli/quota/model-routes`,
    );
  }

  setModelRoute(profile: CliModelRouteProfile) {
    return this.http.put<CliModelRouteProfile>(
      `${this.baseUrl}/cli/quota/model-routes`, profile,
    );
  }

  getModelRoutingRecommendation(taskType: string, cliType: string) {
    return this.http.get<ModelRoutingRecommendation>(
      `${this.baseUrl}/cli/model-routing/recommendation`,
      { params: { taskType, cliType } },
    );
  }

  getModelRoutingPolicy() {
    return this.http.get<ModelRoutingPolicyView>(
      `${this.baseUrl}/cli/model-routing/policy`,
    );
  }

  setModelRoutingEconomyMode(enabled: boolean) {
    return this.http.put<{ economyMode: boolean }>(
      `${this.baseUrl}/cli/model-routing/economy-mode`, { enabled },
    );
  }
}
