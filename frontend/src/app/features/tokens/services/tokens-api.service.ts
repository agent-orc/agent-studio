import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type {
  AdHocUsageAggregate,
  TokenSummary,
  TokenSummaryAggregate,
  TokenTimeline,
  WorkspaceExpensiveJobsResponse,
} from '../models/tokens.model';
import type { UsageLedgerScope } from '../../usage-cockpit';

/**
 * Cycle 10d API client for the token-aggregate endpoints. Lifted out
 * of the TaskService god-service per ADR-0034 so the per-feature HTTP
 * surface is owned by the feature folder. The central TaskService
 * keeps the job-lifecycle methods + grouped state; pure read-only
 * token aggregates live here.
 *
 * Wraps `/api/runner/.../token-summary*`, `/api/adhoc-usage`, and
 * `/api/workspace/tokens/timeline`.
 */
@Injectable({ providedIn: 'root' })
export class TokensApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api';

  /** Per-project token rollup. */
  getTokenSummary(projectName: string) {
    return this.http.get<TokenSummary>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/token-summary`,
    );
  }

  /**
   * Workspace-wide token aggregate. Forces a fresh scan across all
   * watched projects and writes the result to the on-disk cache, so
   * the next cached call returns it instantly. Cheap (reads JSONL
   * files only); safe to poll.
   */
  getTokenSummaryAggregate() {
    return this.http.get<TokenSummaryAggregate>(
      `${this.baseUrl}/runner/token-summary-aggregate`,
    );
  }

  /**
   * Cache-only read of the workspace-wide aggregate. Returns
   * immediately with the on-disk snapshot without re-scanning the
   * orchestrator logs. The status-bar usage modal calls this on first
   * paint so the user sees real numbers before the live aggregator
   * finishes; 204 No Content means there is no cached snapshot yet.
   */
  getTokenSummaryAggregateCached() {
    return this.http.get<TokenSummaryAggregate>(
      `${this.baseUrl}/runner/token-summary-aggregate/cached`,
      { observe: 'response' },
    );
  }

  /**
   * Workspace-wide ad-hoc Haiku usage rollup. Powers the "Ad-hoc CLI
   * usage" section in the status-bar hover panel. Cheap (reads one
   * JSONL file); safe to poll alongside the project-token aggregate.
   */
  getAdHocUsage() {
    return this.http.get<AdHocUsageAggregate>(`${this.baseUrl}/adhoc-usage/`);
  }

  /**
   * Workspace-wide token timeline: one cell per (project, time-bucket).
   * `windowHours` accepts {1, 6, 24, 168}; `bucketMinutes` accepts
   * {5, 15, 60}. Out-of-range values are silently snapped to the
   * defaults by the backend.
   */
  getWorkspaceTokensTimeline(windowHours: number, bucketMinutes: number, scope: UsageLedgerScope | null = null) {
    let params = new HttpParams()
      .set('windowHours', String(windowHours))
      .set('bucketMinutes', String(bucketMinutes));
    if (scope) {
      params = params.set('workspaceId', scope.workspaceId)
        .set('fromUtc', scope.fromUtc).set('toUtc', scope.toUtc);
      if (scope.projectId) params = params.set('projectId', scope.projectId);
    }
    return this.http.get<TokenTimeline>(
      `${this.baseUrl}/workspace/tokens/timeline`,
      { params },
    );
  }

  /**
   * Cache-only timeline read. Returns the on-disk snapshot for the
   * given (windowHours, bucketMinutes) combo without re-folding the
   * workspace bus. The status-bar hover modal calls this on first
   * paint so the historical sparklines appear instantly; 204 No
   * Content means no cached snapshot exists yet.
   */
  getWorkspaceTokensTimelineCached(windowHours: number, bucketMinutes: number) {
    const params = new HttpParams()
      .set('windowHours', String(windowHours))
      .set('bucketMinutes', String(bucketMinutes));
    return this.http.get<TokenTimeline>(
      `${this.baseUrl}/workspace/tokens/timeline/cached`,
      { params, observe: 'response' },
    );
  }

  /** Top token-consuming jobs folded across every watched project. */
  getWorkspaceExpensiveJobs(limit = 8) {
    const params = new HttpParams().set('limit', String(limit));
    return this.http.get<WorkspaceExpensiveJobsResponse>(
      `${this.baseUrl}/workspace/tokens/expensive-jobs`,
      { params },
    );
  }

  /**
   * Cache-only expensive-jobs read. Same instant-first-paint pattern
   * as the cached timeline endpoint.
   */
  getWorkspaceExpensiveJobsCached() {
    return this.http.get<WorkspaceExpensiveJobsResponse>(
      `${this.baseUrl}/workspace/tokens/expensive-jobs/cached`,
      { observe: 'response' },
    );
  }
}
