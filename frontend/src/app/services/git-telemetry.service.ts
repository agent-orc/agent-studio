import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export interface GitEndpointStats {
  label: string;
  sampleCount: number;
  p50Ms: number;
  p95Ms: number;
  totalSpawns: number;
  spawnsPerMinute: number;
}

export interface GitStateRepositoryStatus {
  projectName: string;
  gitStateAt: string | null;
  refreshing: boolean;
  ageSeconds: number | null;
}

export interface GitTelemetrySnapshot {
  generatedAt: string;
  endpoints: GitEndpointStats[];
  repositories: GitStateRepositoryStatus[];
  totalSpawnsPerMinute: number;
  warnings: string[];
}

/**
 * Admin surface for the AGT-2726 board-performance SLO: per-endpoint git-spawn
 * p50/p95/rate (read from the backend's `GitProcessTelemetry` rolling window)
 * and per-repository background-index freshness. Polled on demand rather than
 * kept live - it is a diagnostic panel, not a board surface.
 */
@Injectable({ providedIn: 'root' })
export class GitTelemetryService {
  private readonly http = inject(HttpClient);

  readonly snapshot = signal<GitTelemetrySnapshot | null>(null);
  readonly loadError = signal<string | null>(null);

  async load(): Promise<void> {
    try {
      const resp = await firstValueFrom(
        this.http.get<GitTelemetrySnapshot>('/api/admin/git-telemetry')
      );
      this.snapshot.set(resp);
      this.loadError.set(null);
    } catch (err: unknown) {
      this.loadError.set(err instanceof Error ? err.message : 'Failed to load Git telemetry');
    }
  }
}
