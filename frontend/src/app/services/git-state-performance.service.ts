import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** One telemetry label's latency and git cost over the reporting window. */
export interface GitStateEndpointSummary {
  readonly label: string;
  readonly calls: number;
  readonly p50Ms: number;
  readonly p95Ms: number;
  readonly spawns: number;
  readonly gitMs: number;
}

/** How old one repository's indexed state is, and what its last capture cost. */
export interface GitStateRepositoryStatus {
  readonly repositoryRoot: string;
  readonly projectNames: readonly string[];
  readonly gitStateAt: string | null;
  readonly ageSeconds: number | null;
  readonly lastTrigger: string | null;
  readonly lastRunMs: number;
  readonly lastRunSpawns: number;
  readonly refreshing: boolean;
}

export interface GitStateIndexRun {
  readonly repositoryRoot: string;
  readonly trigger: string;
  readonly completedAtUtc: string;
  readonly elapsedMs: number;
  readonly spawns: number;
  readonly succeeded: boolean;
}

export interface GitStateSloWarning {
  readonly code: string;
  readonly message: string;
}

export interface GitStatePerformance {
  readonly windowStartUtc: string;
  readonly windowEndUtc: string;
  readonly endpoints: readonly GitStateEndpointSummary[];
  readonly repositories: readonly GitStateRepositoryStatus[];
  readonly recentRuns: readonly GitStateIndexRun[];
  readonly spawns: number;
  readonly spawnsPerMinute: number;
  readonly warnings: readonly GitStateSloWarning[];
}

/**
 * Reads the backend's performance rollup for the board's git-derived state
 * (AGT-2726). The numbers come from `GitProcessTelemetry` and the background git
 * index, which are also what the logs report; this endpoint adds no counter of
 * its own.
 */
@Injectable({ providedIn: 'root' })
export class GitStatePerformanceService {
  private readonly http = inject(HttpClient);

  load(): Observable<GitStatePerformance> {
    return this.http.get<GitStatePerformance>('/api/admin/performance/git-state');
  }
}
