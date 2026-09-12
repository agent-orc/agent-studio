import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, catchError, finalize, map, switchMap, tap, throwError } from 'rxjs';
import {
  ConfirmWorkbenchDecisionRequest,
  PrepareWorkbenchDecisionRequest,
  WorkbenchDecisionResult,
} from '../../../models/project-docs.model';

export interface WorkbenchDecisionRequestState {
  pending: 'prepare' | 'confirm' | null;
  result: WorkbenchDecisionResult | null;
  error: string | null;
}

const EMPTY_STATE: WorkbenchDecisionRequestState = {
  pending: null,
  result: null,
  error: null,
};

@Injectable({ providedIn: 'root' })
export class WorkbenchDecisionStore {
  private readonly http = inject(HttpClient);
  private readonly entries = signal<Record<string, WorkbenchDecisionRequestState>>({});

  state(projectName: string, workbenchId: string): WorkbenchDecisionRequestState {
    return this.entries()[this.key(projectName, workbenchId)] ?? EMPTY_STATE;
  }

  prepare(
    projectName: string,
    workbenchId: string,
    request: PrepareWorkbenchDecisionRequest,
  ): Observable<WorkbenchDecisionResult> {
    return this.mutate(projectName, workbenchId, 'prepare', request);
  }

  confirm(
    projectName: string,
    workbenchId: string,
    request: ConfirmWorkbenchDecisionRequest,
  ): Observable<WorkbenchDecisionResult> {
    return this.mutate(projectName, workbenchId, 'confirm', request);
  }

  requestRework(
    projectName: string,
    workbenchId: string,
    workbenchKey: string,
    prepareRequest: PrepareWorkbenchDecisionRequest,
    prompt: string,
    agent: { cliType: string; model: string; thinkingLevel: string | null },
  ): Observable<WorkbenchDecisionResult> {
    return this.prepare(projectName, workbenchId, prepareRequest).pipe(
      switchMap(prepared => this.confirm(projectName, workbenchId, {
        ...prepareRequest,
        expectedRevision: prepared.revision,
        expectedFingerprint: prepared.fingerprint,
        spawnedTaskKeys: [],
        cliType: agent.cliType,
        model: agent.model,
        thinkingLevel: agent.thinkingLevel,
        confirmed: true,
      })),
      switchMap(confirmed => this.http.post<unknown>(
        `/api/orchestrator/sessions/workbench:${encodeURIComponent(projectName)}/${encodeURIComponent(workbenchKey)}/turns`,
        {
          prompt,
          cliType: agent.cliType,
          model: agent.model,
          thinkingLevel: agent.thinkingLevel,
        },
      ).pipe(map(() => confirmed))),
    );
  }

  clear(projectName: string, workbenchId: string): void {
    const key = this.key(projectName, workbenchId);
    this.entries.update(entries => {
      const next = { ...entries };
      delete next[key];
      return next;
    });
  }

  private mutate(
    projectName: string,
    workbenchId: string,
    phase: 'prepare' | 'confirm',
    request: PrepareWorkbenchDecisionRequest | ConfirmWorkbenchDecisionRequest,
  ): Observable<WorkbenchDecisionResult> {
    const key = this.key(projectName, workbenchId);
    this.patch(key, { pending: phase, error: null });
    const url = `/api/projects/${encodeURIComponent(projectName)}/workbenches/${encodeURIComponent(workbenchId)}/decisions/${phase}`;
    return this.http.post<WorkbenchDecisionResult>(url, request).pipe(
      tap(result => this.patch(key, { result, error: null })),
      catchError((error: HttpErrorResponse) => {
        this.patch(key, { error: decisionError(error) });
        return throwError(() => error);
      }),
      finalize(() => this.patch(key, { pending: null })),
    );
  }

  private patch(key: string, patch: Partial<WorkbenchDecisionRequestState>): void {
    this.entries.update(entries => ({
      ...entries,
      [key]: { ...(entries[key] ?? EMPTY_STATE), ...patch },
    }));
  }

  private key(projectName: string, workbenchId: string): string {
    return `${projectName}\u0000${workbenchId}`;
  }
}

function decisionError(error: HttpErrorResponse): string {
  const payload = error.error as Partial<WorkbenchDecisionResult> | null;
  if (payload?.error) return payload.error;
  if (error.status === 0) return 'The decision service is unavailable.';
  return 'The Dossier decision could not be persisted.';
}
