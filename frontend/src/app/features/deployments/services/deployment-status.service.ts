import { HttpClient } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { catchError, from, map, mergeMap, of, scan } from 'rxjs';
import type { EnvironmentStatus } from '../models/deployment.model';
import type { DeploymentUrl, DeployHistoryEntry, InstallationOverview, UrlObservation } from '../models/observed-deployment.model';

export const DEPLOYMENT_CLOCK = new InjectionToken<() => number>('deployment clock', {
  providedIn: 'root', factory: () => Date.now,
});

/** One row per project this instance watches; a registered but unwatched project has none. */
export interface DeployMatrixRow {
  projectId: string;
  statuses: EnvironmentStatus[];
  tenantStatuses: EnvironmentStatus[];
  error: string | null;
  /** False when the project repository declares no environment; absent on older servers. */
  environmentsDeclared?: boolean;
  installation?: InstallationOverview | null;
  urls?: DeploymentUrl[];
}

export interface DeployMatrixResponse {
  environmentKinds: string[];
  hasDeployRecords?: boolean;
  projects: DeployMatrixRow[];
}

@Injectable({ providedIn: 'root' })
export class DeploymentStatusService {
  private readonly http = inject(HttpClient);

  urlObservations(urls: DeploymentUrl[]) {
    return from(urls).pipe(mergeMap(url => this.http.get<UrlObservation>(
      `/api/projects/${encodeURIComponent(url.projectId)}/urls/${encodeURIComponent(url.id)}/readiness?buildStamp=true`,
    ).pipe(catchError(() => of<UrlObservation>({ kind: 'unavailable', statusCode: null, commit: null })),
      map(facts => ({ key: url.projectId + '/' + url.id, facts }))), 3),
      scan((known, { key, facts }) => new Map(known).set(key, facts), new Map<string, UrlObservation>()));
  }

  deployRecords(offset: number) {
    type RecordItem = Omit<DeployHistoryEntry, 'atUtc' | 'cardCount' | 'steps' | 'outcome'> & {
      cardsShipped: string[] | null; outcome: string | null;
      steps: (Omit<DeployHistoryEntry['steps'][number], 'outcome'> & { outcome: string | null })[] | null;
    };
    return this.http.get<{ items: RecordItem[]; total: number }>(
      '/api/operations/deploy-records', { params: { offset, limit: 25 } }).pipe(map(page => ({
      total: page.total, items: page.items.map(record => ({
        ...record, atUtc: record.startedAtUtc!, cardCount: record.cardsShipped?.length ?? null,
        outcome: record.outcome ?? 'unknown outcome',
        steps: (record.steps ?? []).map(step => ({ ...step, outcome: step.outcome ?? 'not observed' })),
      })),
    })));
  }

  matrix() {
    return this.http.get<DeployMatrixResponse>('/api/deploy/matrix');
  }
}
