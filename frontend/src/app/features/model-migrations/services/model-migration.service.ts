import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, finalize, map, of, shareReplay, tap } from 'rxjs';
import type {
  ApplyModelMigrationRequest,
  ModelMigrationProposal,
  ModelMigrationSnapshot,
} from '../models/model-migration.model';

/**
 * Process-wide cache for model migration proposals. Task cards share the
 * workspace snapshot so rendering a full board never fans out into one HTTP
 * request per card. Project pipeline pages additionally keep a project-scoped
 * snapshot, which avoids pulling unrelated project settings into the editor.
 */
@Injectable({ providedIn: 'root' })
export class ModelMigrationService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/model-migrations';
  private workspaceRequest: Observable<ModelMigrationSnapshot> | null = null;
  private readonly projectRequests = new Map<string, Observable<ModelMigrationSnapshot>>();

  readonly workspace = signal<ModelMigrationSnapshot | null>(null);
  readonly projects = signal<Record<string, ModelMigrationSnapshot>>({});
  readonly loadingWorkspace = signal(false);
  readonly savingAutoApply = signal(false);
  readonly error = signal<string | null>(null);
  private readonly applyingKeys = signal<ReadonlySet<string>>(new Set());

  readonly configurationProposals = computed(() =>
    (this.workspace()?.proposals ?? []).filter((proposal) => proposal.scope === 'configuration'),
  );

  ensureWorkspaceLoaded(): void {
    this.loadWorkspace().subscribe({ error: () => void 0 });
  }

  loadWorkspace(force = false): Observable<ModelMigrationSnapshot> {
    const current = this.workspace();
    if (!force && current) return of(current);
    if (!force && this.workspaceRequest) return this.workspaceRequest;

    this.loadingWorkspace.set(true);
    this.error.set(null);
    const request = this.http.get<ModelMigrationSnapshot>(this.baseUrl).pipe(
      tap((snapshot) => this.workspace.set(normalizeSnapshot(snapshot))),
      tap({ error: () => this.error.set('Could not load model migration proposals.') }),
      finalize(() => {
        if (this.workspaceRequest === request) this.workspaceRequest = null;
        this.loadingWorkspace.set(false);
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    this.workspaceRequest = request;
    return request;
  }

  ensureProjectLoaded(projectName: string): void {
    this.loadProject(projectName).subscribe({ error: () => void 0 });
  }

  loadProject(projectName: string, force = false): Observable<ModelMigrationSnapshot> {
    const key = normalize(projectName);
    const current = this.projects()[key];
    if (!force && current) return of(current);
    const pending = this.projectRequests.get(key);
    if (!force && pending) return pending;

    const params = new HttpParams().set('project', projectName);
    const request = this.http.get<ModelMigrationSnapshot>(this.baseUrl, { params }).pipe(
      tap((snapshot) => {
        this.projects.update((projects) => ({
          ...projects,
          [key]: normalizeSnapshot(snapshot),
        }));
      }),
      tap({ error: () => this.error.set(`Could not load model migrations for ${projectName}.`) }),
      finalize(() => {
        if (this.projectRequests.get(key) === request) this.projectRequests.delete(key);
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    this.projectRequests.set(key, request);
    return request;
  }

  proposalForTask(
    taskId: string,
    projectName: string,
    currentModel?: string | null,
  ): ModelMigrationProposal | null {
    return (this.workspace()?.proposals ?? []).find((proposal) =>
      proposal.scope === 'task'
      && proposal.explicit
      && same(proposal.taskId, taskId)
      && (!proposal.projectName || same(proposal.projectName, projectName))
      && (!currentModel || same(proposal.fromModel, currentModel))) ?? null;
  }

  proposalForPipelineStep(
    projectName: string,
    pipelineType: string,
    stepId: string,
    currentModel?: string | null,
  ): ModelMigrationProposal | null {
    const snapshot = this.projects()[normalize(projectName)] ?? this.workspace();
    return (snapshot?.proposals ?? []).find((proposal) =>
      proposal.scope === 'pipelineStep'
      && same(proposal.projectName, projectName)
      && same(proposal.pipelineType, pipelineType)
      && same(proposal.stepId, stepId)
      && (!currentModel || same(proposal.fromModel, currentModel))) ?? null;
  }

  isApplying(proposal: ModelMigrationProposal): boolean {
    return this.applyingKeys().has(proposalKey(proposal));
  }

  apply(proposal: ModelMigrationProposal): Observable<void> {
    const key = proposalKey(proposal);
    this.updateApplying(key, true);
    this.error.set(null);
    return this.http.post<void>(`${this.baseUrl}/apply`, applyRequest(proposal)).pipe(
      tap(() => {
        this.removeProposal(key);
        this.refreshAfterApply(proposal);
      }),
      tap({ error: () => this.error.set('Could not apply the model migration. Refresh and try again.') }),
      finalize(() => this.updateApplying(key, false)),
    );
  }

  setAutoApply(enabled: boolean, workspaceId?: string | null): Observable<boolean> {
    if (this.savingAutoApply()) return of(this.workspace()?.autoApplySafe ?? enabled);
    this.savingAutoApply.set(true);
    this.error.set(null);
    const body: { enabled: boolean; workspaceId?: string } = { enabled };
    if (workspaceId) body.workspaceId = workspaceId;
    return this.http.put<{ autoApplySafe?: boolean }>(`${this.baseUrl}/auto-apply`, body).pipe(
      map((response) => response?.autoApplySafe ?? enabled),
      tap((autoApplySafe) => {
        this.workspace.update((snapshot) => snapshot ? { ...snapshot, autoApplySafe } : snapshot);
      }),
      tap({ error: () => this.error.set('Could not update automatic model migrations.') }),
      finalize(() => this.savingAutoApply.set(false)),
    );
  }

  private refreshAfterApply(proposal: ModelMigrationProposal): void {
    this.loadWorkspace(true).subscribe({ error: () => void 0 });
    if (proposal.projectName) {
      this.loadProject(proposal.projectName, true).subscribe({ error: () => void 0 });
    }
  }

  private removeProposal(key: string): void {
    this.workspace.update((snapshot) => snapshot ? withoutProposal(snapshot, key) : null);
    this.projects.update((projects) => Object.fromEntries(
      Object.entries(projects).map(([project, snapshot]) => [project, withoutProposal(snapshot, key)]),
    ));
  }

  private updateApplying(key: string, applying: boolean): void {
    this.applyingKeys.update((current) => {
      const next = new Set(current);
      if (applying) next.add(key);
      else next.delete(key);
      return next;
    });
  }
}

export function applyRequest(proposal: ModelMigrationProposal): ApplyModelMigrationRequest {
  return compactUndefined({
    scope: proposal.scope,
    projectName: value(proposal.projectName),
    taskId: value(proposal.taskId),
    pipelineType: value(proposal.pipelineType),
    stepId: value(proposal.stepId),
    configKey: value(proposal.configKey),
    expectedFromModel: proposal.fromModel,
    catalogVersion: proposal.catalogVersion,
  });
}

export function proposalKey(proposal: ModelMigrationProposal): string {
  return [
    proposal.scope,
    proposal.projectName,
    proposal.taskId,
    proposal.pipelineType,
    proposal.stepId,
    proposal.configKey,
    proposal.fromModel,
    proposal.toModel,
    proposal.catalogVersion,
  ].map(normalize).join('|');
}

function normalizeSnapshot(snapshot: ModelMigrationSnapshot): ModelMigrationSnapshot {
  return { ...snapshot, proposals: snapshot?.proposals ?? [] };
}

function withoutProposal(
  snapshot: ModelMigrationSnapshot,
  key: string,
): ModelMigrationSnapshot {
  return {
    ...snapshot,
    proposals: snapshot.proposals.filter((proposal) => proposalKey(proposal) !== key),
  };
}

function value(input: string | null | undefined): string | undefined {
  const trimmed = input?.trim();
  return trimmed ? trimmed : undefined;
}

function normalize(input: unknown): string {
  return String(input ?? '').trim().toLowerCase();
}

function same(left: unknown, right: unknown): boolean {
  return normalize(left) === normalize(right);
}

function compactUndefined<T extends object>(input: T): T {
  return Object.fromEntries(
    Object.entries(input).filter(([, fieldValue]) => fieldValue !== undefined),
  ) as T;
}
