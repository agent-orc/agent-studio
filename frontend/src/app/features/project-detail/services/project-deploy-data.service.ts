import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, startWith } from 'rxjs';
import { TaskService } from '../../../services/task.service';
import type { ProjectEnvironments } from '../../../models/project-overview.model';
import {
  DEPLOY_HEALTH_CHECKS,
  type DeployHealthCheckSummary,
  type DeployLifecycleRead,
  type DeployPanel,
  type Release,
} from '../state/deploy-area.model';

/**
 * Reads for the project Deploy area. Every panel loads on its own, so one
 * missing or failing source leaves the others intact. Nothing here invents
 * data: a source that is not on this server reports `unavailable`.
 */
@Injectable({ providedIn: 'root' })
export class ProjectDeployDataService {
  private readonly http = inject(HttpClient);
  private readonly tasks = inject(TaskService);
  private readonly healthChecks = inject(DEPLOY_HEALTH_CHECKS);

  /** Declared environments, history, promotions, and the deploy job queue (AGT-3176). */
  environments(projectName: string): Observable<DeployPanel<ProjectEnvironments>> {
    return panel(this.tasks.getProjectEnvironments(projectName), 'Environments could not be loaded.', false);
  }

  /** Releases of the shared P1 contract; a 404 means the release service is not deployed yet. */
  releases(projectName: string): Observable<DeployPanel<Release[]>> {
    const request = this.http.get<Release[] | { releases: Release[] }>(`/api/projects/${encodeURIComponent(projectName)}/releases`)
      .pipe(map(body => Array.isArray(body) ? body : body.releases ?? []));
    return panel(request, 'Releases could not be loaded.', true);
  }

  healthSummaries(projectName: string): Observable<DeployPanel<DeployHealthCheckSummary[]>> {
    if (!this.healthChecks) return of({ status: 'unavailable' });
    return panel(this.healthChecks.summaries(projectName), 'Health checks could not be loaded.', true);
  }

  /** The project.yml lifecycle projection served with the execution definition. */
  lifecycle(projectName: string): Observable<DeployPanel<DeployLifecycleRead>> {
    const request = this.http.get<DeployLifecycleRead>(`/api/projects/${encodeURIComponent(projectName)}/execution`);
    return panel(request, 'The project.yml lifecycle could not be loaded.', false);
  }
}

function panel<T>(source: Observable<T>, message: string, notFoundIsUnavailable: boolean): Observable<DeployPanel<T>> {
  return source.pipe(
    map(value => ({ status: 'ready', value }) as DeployPanel<T>),
    catchError((error: unknown) => of<DeployPanel<T>>(
      notFoundIsUnavailable && error instanceof HttpErrorResponse && error.status === 404
        ? { status: 'unavailable' }
        : { status: 'error', message })),
    startWith<DeployPanel<T>>({ status: 'loading' }),
  );
}
