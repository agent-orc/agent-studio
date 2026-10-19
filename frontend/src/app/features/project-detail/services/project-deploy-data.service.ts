import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, startWith, throwError } from 'rxjs';
import { TaskService } from '../../../services/task.service';
import type { ProjectEnvironments } from '../../../models/project-overview.model';
import type {
  DeployPanel,
  DeployRunAction,
  Deployment,
  DeploymentLogLine,
  DeploymentLogResponse,
  EnvironmentStatus,
  Release,
} from '../state/deploy-area.model';
import {
  pipelineFromDefinition,
  pipelineFromExecution,
  type DeployPipelineView,
  type ExecutionLifecycleRead,
  type PipelineDefinitionRead,
} from '../state/deploy-pipeline.model';

/**
 * A 404 without an `error` body: the route is not on this server. AGT-3201 and
 * AGT-3221 answer an unknown project or deployment with `{ error }`, which
 * stays an error.
 */
export function isMissingRoute(error: unknown): boolean {
  if (!(error instanceof HttpErrorResponse) || error.status !== 404) return false;
  const body: unknown = error.error;
  return !(body && typeof body === 'object' && typeof (body as { error?: unknown }).error === 'string');
}

/** The server's `{ error }` message, if it sent one. */
export function serverMessage(error: unknown): string | null {
  const body: unknown = error instanceof HttpErrorResponse ? error.error : null;
  const message = body && typeof body === 'object' ? (body as { error?: unknown }).error : null;
  return typeof message === 'string' && message.trim() ? message : null;
}

/**
 * Reads for the project Deploy area. Every panel loads on its own, so one
 * missing or failing source leaves the others intact. Nothing here invents
 * data: a route that is not on this server reports `unavailable`, and every
 * other failure reports `error`.
 */
@Injectable({ providedIn: 'root' })
export class ProjectDeployDataService {
  private readonly http = inject(HttpClient);
  private readonly tasks = inject(TaskService);

  /** Declared environments, host history, and promotions (AGT-3176). */
  environments(projectName: string): Observable<DeployPanel<ProjectEnvironments>> {
    return panel(this.tasks.getProjectEnvironments(projectName), 'Environments could not be loaded.', false);
  }

  /** One AGT-3201 status per environment: current and newer release, latest run, health. */
  statuses(projectName: string): Observable<DeployPanel<EnvironmentStatus[]>> {
    return panel(this.http.get<EnvironmentStatus[]>(`${deployRoot(projectName)}/status`),
      'Environment status could not be loaded.', true);
  }

  releases(projectName: string): Observable<DeployPanel<Release[]>> {
    return panel(this.http.get<Release[]>(`${deployRoot(projectName)}/releases`), 'Releases could not be loaded.', true);
  }

  /** AGT-3201 deploy runs, newest first. */
  deployments(projectName: string): Observable<DeployPanel<Deployment[]>> {
    return panel(this.http.get<Deployment[]>(`${deployRoot(projectName)}/deployments`),
      'Deploy runs could not be loaded.', true);
  }

  deploymentLog(projectName: string, deploymentId: string): Observable<DeployPanel<DeploymentLogLine[]>> {
    const request = this.http
      .get<DeploymentLogResponse>(`${deployRoot(projectName)}/deployments/${encodeURIComponent(deploymentId)}/log`)
      .pipe(map(body => body.lines));
    return panel(request, 'The step log could not be loaded.', true);
  }

  /** Approve, reject, or cancel a run; the server checks role and state. */
  act(projectName: string, deploymentId: string, action: DeployRunAction): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${deployRoot(projectName)}/deployments/${encodeURIComponent(deploymentId)}/${action}`, {});
  }

  /** AGT-3221's pipeline diagram when the server has it; this server's lifecycle projection otherwise. */
  pipeline(projectName: string): Observable<DeployPanel<DeployPipelineView>> {
    const project = encodeURIComponent(projectName);
    const execution = this.http.get<ExecutionLifecycleRead>(`/api/projects/${project}/execution`)
      .pipe(map(pipelineFromExecution));
    const request = this.http.get<PipelineDefinitionRead>(`/api/projects/${project}/pipeline-definition`).pipe(
      map(pipelineFromDefinition),
      catchError((error: unknown) => isMissingRoute(error) ? execution : throwError(() => error)));
    return panel(request, 'The project.yml pipeline could not be loaded.', false);
  }
}

function deployRoot(projectName: string): string {
  return `/api/projects/${encodeURIComponent(projectName)}/deploy`;
}

function panel<T>(source: Observable<T>, message: string, missingRouteIsUnavailable: boolean): Observable<DeployPanel<T>> {
  return source.pipe(
    map(value => ({ status: 'ready', value }) as DeployPanel<T>),
    catchError((error: unknown) => of<DeployPanel<T>>(
      missingRouteIsUnavailable && isMissingRoute(error)
        ? { status: 'unavailable' }
        : { status: 'error', message: serverMessage(error) ?? message })),
    startWith<DeployPanel<T>>({ status: 'loading' }),
  );
}
