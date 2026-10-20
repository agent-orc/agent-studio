import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { type Observable, map, of, catchError } from 'rxjs';

/** One version as metadata. The API has no value field on any human route. */
export interface SecretVersion {
  version: number;
  origin: 'put' | 'rotate';
  active: boolean;
  createdAt: string;
  createdBy: string;
  revokedAt: string | null;
  revokedBy: string | null;
  lastUsedAt: string | null;
}

export interface SecretMetadata {
  reference: string;
  scope: string;
  scopeKind: 'workspace' | 'project' | 'environment';
  name: string;
  status: 'active' | 'revoked';
  currentVersion: number | null;
  createdAt: string;
  createdBy: string;
  updatedAt: string;
  updatedBy: string;
  rotatedAt: string | null;
  rotatedBy: string | null;
  lastUsedAt: string | null;
  lastUsedBy: string | null;
  lastUsedPurpose: string | null;
  versions: readonly SecretVersion[];
}

/** A stored item that failed its key or integrity check. Scope and name are unverified claims. */
export interface UnreadableSecret {
  id: string;
  scope: string | null;
  name: string | null;
  problem: 'key-unavailable' | 'integrity-failed' | 'read-failed';
  reason: string;
}

export interface SecretOverview {
  store: string;
  keySource: string;
  canWriteWorkspace: boolean;
  secrets: readonly SecretMetadata[];
  unreadable: readonly UnreadableSecret[];
}

/** `workspace`, `<project>` or `<project>/<environment>`. */
export type SecretScopeKey = string;

export interface SecretEnvironment { id: string; kind: string }

/** Route prefix of one scope, matching the backend scope groups. */
export function secretScopeRoute(scope: SecretScopeKey): string {
  if (scope === 'workspace') return '/api/secrets/workspace';
  const [project, environment] = scope.split('/', 2);
  const base = `/api/projects/${encodeURIComponent(project)}`;
  return environment
    ? `${base}/environments/${encodeURIComponent(environment)}/secrets`
    : `${base}/secrets`;
}

/**
 * Secrets in the Access area of Settings. Values are write-only: they leave
 * the browser in a put or rotate request and are never read back.
 */
@Injectable({ providedIn: 'root' })
export class SecretsService {
  private readonly http = inject(HttpClient, { optional: true });
  readonly overview = signal<SecretOverview | null>(null);
  readonly state = signal<'idle' | 'loading' | 'ready' | 'unavailable'>('idle');
  readonly error = signal<string | null>(null);

  load(): void {
    if (!this.http) {
      this.state.set('unavailable');
      return;
    }
    this.state.set('loading');
    this.http.get<SecretOverview>('/api/secrets').subscribe({
      next: overview => {
        this.overview.set(overview);
        this.state.set('ready');
      },
      error: (error: unknown) => {
        this.overview.set(null);
        this.error.set(messageOf(error));
        this.state.set('unavailable');
      },
    });
  }

  /** Declarations carry kind separately from id, including custom Production ids. */
  environments(project: string): Observable<readonly SecretEnvironment[]> {
    if (!this.http) return of([]);
    return this.http.get<{ environments: readonly { declaration: SecretEnvironment }[] }>(
      `/api/projects/${encodeURIComponent(project)}/environments`,
    ).pipe(
      map(response => response.environments.map(item => item.declaration)),
      catchError(() => of([] as readonly SecretEnvironment[])),
    );
  }

  add(scope: SecretScopeKey, name: string, value: string): Observable<SecretMetadata> {
    return this.mutate(this.http!.post<SecretMetadata>(secretScopeRoute(scope), { name, value }));
  }

  rotate(scope: SecretScopeKey, name: string, value: string): Observable<SecretMetadata> {
    return this.mutate(this.http!.post<SecretMetadata>(
      `${secretScopeRoute(scope)}/${encodeURIComponent(name)}/rotate`, { value }));
  }

  revoke(scope: SecretScopeKey, name: string, version: number | null): Observable<SecretMetadata> {
    return this.mutate(this.http!.post<SecretMetadata>(
      `${secretScopeRoute(scope)}/${encodeURIComponent(name)}/revoke`, { version }));
  }

  private mutate(request: Observable<SecretMetadata>): Observable<SecretMetadata> {
    this.error.set(null);
    return request.pipe(
      map(record => {
        this.merge(record);
        return record;
      }),
      catchError((error: unknown) => {
        this.error.set(messageOf(error));
        throw error;
      }),
    );
  }

  private merge(record: SecretMetadata): void {
    const current = this.overview();
    if (!current) return;
    const others = current.secrets.filter(item => item.reference !== record.reference);
    this.overview.set({ ...current, secrets: [...others, record] });
  }
}

function messageOf(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { message?: string } | null;
    return body?.message ?? `Request failed (${error.status}).`;
  }
  return 'Request failed.';
}
