import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, startWith } from 'rxjs';
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
  DeployRunFilter,
} from '../state/deploy-area.model';
import { ProjectPipelineDefinitionService, type PipelineDefinitionResponse } from './project-pipeline-definition.service';
import { EnvironmentHealthService } from '../../environment-health/services/environment-health.service';
import type { EnvironmentHealthResponse } from '../../environment-health/models/environment-health.model';

export interface DeployPage<T> { items: T[]; offset: number; limit: number; total: number }

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
  private readonly definitions = inject(ProjectPipelineDefinitionService);
  private readonly healthService = inject(EnvironmentHealthService);

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

  releasePage(projectName: string, offset: number): Observable<DeployPanel<DeployPage<Release>>> {
    return panel(this.http.get<DeployPage<Release>>(`${deployRoot(projectName)}/releases/page`,
      { params: { offset, limit: 25 } }), 'Releases could not be loaded.', true);
  }

  /**
   * The newest release that carries the artifact's digest and existed at `notAfterUtc`,
   * one bounded read instead of scanning the release history for a host artifact.
   */
  releaseForArtifact(projectName: string, artifact: string, notAfterUtc: string | null): Observable<DeployPanel<Release | null>> {
    const params: Record<string, string | number> = { offset: 0, limit: 1, artifact };
    if (notAfterUtc) params['notAfterUtc'] = notAfterUtc;
    const request = this.http.get<DeployPage<Release>>(`${deployRoot(projectName)}/releases/page`, { params })
      .pipe(map(page => page.items[0] ?? null));
    return panel(request, 'The release of the deployed artifact could not be loaded.', true);
  }

  /** AGT-3201 deploy runs, newest first. */
  deployments(projectName: string): Observable<DeployPanel<Deployment[]>> {
    return panel(this.http.get<Deployment[]>(`${deployRoot(projectName)}/deployments`),
      'Deploy runs could not be loaded.', true);
  }

  deploymentPage(projectName: string, offset: number, filter?: DeployRunFilter, now?: number): Observable<DeployPanel<DeployPage<Deployment>>> {
    const params: Record<string, string | number> = { offset, limit: 25 };
    if (filter?.environmentId && filter.environmentId !== 'all') params['environmentId'] = filter.environmentId;
    if (filter?.state && filter.state !== 'all') params['state'] = filter.state;
    if (filter?.period && filter.period !== 'all' && now !== undefined) {
      const days = filter.period === '24h' ? 1 : filter.period === '7d' ? 7 : 30;
      params['sinceUtc'] = new Date(now - days * 86_400_000).toISOString();
    }
    return panel(this.http.get<DeployPage<Deployment>>(`${deployRoot(projectName)}/deployments/page`, { params }),
      'Deploy runs could not be loaded.', true);
  }

  /** One run by id, for a run that no loaded page contains. */
  deployment(projectName: string, deploymentId: string): Observable<DeployPanel<Deployment>> {
    return panel(this.http.get<Deployment>(`${deployRoot(projectName)}/deployments/${encodeURIComponent(deploymentId)}`),
      'The deploy run could not be loaded.', true);
  }

  deploymentLog(projectName: string, deploymentId: string): Observable<DeployPanel<DeploymentLogLine[]>> {
    const request = this.http
      .get<DeploymentLogResponse>(`${deployRoot(projectName)}/deployments/${encodeURIComponent(deploymentId)}/log`)
      .pipe(map(body => body.lines));
    return panel(request, 'The step log could not be loaded.', true);
  }

  /** Approve, reject, or cancel a run; the server checks role and state. */
  act(projectName: string, deploymentId: string, action: DeployRunAction, reason?: string): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${deployRoot(projectName)}/deployments/${encodeURIComponent(deploymentId)}/${
        action === 'retry' || action === 'skip' || action === 'abort' ? 'intervention' : action}`,
      action === 'retry' || action === 'skip' || action === 'abort' ? { action, reason } : {});
  }

  pipeline(projectName: string): Observable<DeployPanel<PipelineDefinitionResponse>> {
    return panel(this.definitions.read(projectName), 'The project.yml pipeline could not be loaded.', true);
  }

  health(projectName: string): Observable<DeployPanel<EnvironmentHealthResponse>> {
    return panel(this.healthService.project(projectName), 'Environment health could not be loaded.', true);
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
