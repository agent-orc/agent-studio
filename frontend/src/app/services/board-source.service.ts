import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export interface BoardShadowDifference {
  fingerprint: string;
  project: string;
  taskKey: string;
  field: string;
  workspaceValue: string | null;
  taskServerValue: string | null;
  firstSeenAt: string;
  lastSeenAt: string;
  occurrences: number;
  present: boolean;
  explainedAt: string | null;
  explanation: string | null;
  explainedBy: string | null;
}

export interface BoardSourceStatus {
  source: 'workspace' | 'task-server-shadow' | 'task-server';
  rejectedSetting: string | null;
  mirroring: boolean;
  sync: {
    syncedGeneration: number | null;
    syncedAt: string | null;
    lastError: string | null;
    lastErrorAt: string | null;
    missingProjects: string[];
    rejectedCards: number;
  };
  comparisonRequests: number;
  comparisons: number;
  skippedComparisons: number;
  lastComparisonAt: string | null;
  lastComparedGeneration: number | null;
  unexplainedDifferences: number;
  explainedDifferences: number;
  differences: BoardShadowDifference[];
  readiness: {
    state: 'shadow-off' | 'no-comparisons' | 'comparisons-stalled' | 'clean-window-open' | 'ready' | 'switched';
    ready: boolean;
    cleanSince: string | null;
    cleanDays: number;
    requiredDays: number;
  };
  boardRead: {
    sampleCount: number;
    p95Ms: number | null;
    spawnsPerRequest: number | null;
    targetP95Ms: number;
    targetSpawnsPerRequest: number;
    met: boolean;
  } | null;
}

/**
 * Board source metrics (pipeline delivery target M1 and M2): which source
 * answers the board, the Task Server mirror, the shadow differences by card
 * and field, and the 7-day cutover gate. Loaded on demand; it is a
 * diagnostic card, not a board surface.
 */
@Injectable({ providedIn: 'root' })
export class BoardSourceService {
  private readonly http = inject(HttpClient);

  readonly status = signal<BoardSourceStatus | null>(null);
  readonly loadError = signal<string | null>(null);

  async load(): Promise<void> {
    try {
      this.status.set(await firstValueFrom(this.http.get<BoardSourceStatus>('/api/admin/board-source')));
      this.loadError.set(null);
    } catch (err: unknown) {
      this.loadError.set(err instanceof Error ? err.message : 'Failed to load the board source');
    }
  }

  async explain(fingerprint: string, explanation: string): Promise<void> {
    try {
      this.status.set(await firstValueFrom(this.http.post<BoardSourceStatus>(
        '/api/admin/board-source/differences/explain', { fingerprint, explanation })));
      this.loadError.set(null);
    } catch (err: unknown) {
      this.loadError.set(err instanceof Error ? err.message : 'Failed to record the explanation');
    }
  }
}
