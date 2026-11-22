import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, startWith } from 'rxjs';
import type { DeployPanel } from '../state/deploy-area.model';
import type { InstanceDeployView } from '../state/instance-deploy.model';
import { serverMessage } from './project-deploy-data.service';

/** Reads the running Studio instance for the Bereitstellung overview (AGT-3879). */
@Injectable({ providedIn: 'root' })
export class InstanceDeployDataService {
  private readonly http = inject(HttpClient);

  /**
   * The running Studio instance when this is its source project. Any 404
   * means "not this project" or "route not on this server", and an answer
   * that is not an instance view (a proxy or fallback body) says nothing about
   * the instance: all of them render nothing.
   */
  instance(projectName: string): Observable<DeployPanel<InstanceDeployView>> {
    return this.http.get<unknown>(`/api/projects/${encodeURIComponent(projectName)}/deploy/instance`).pipe(
      map(value => (isInstanceView(value) ? { status: 'ready', value } : { status: 'unavailable' }) as DeployPanel<InstanceDeployView>),
      catchError((error: unknown) => of<DeployPanel<InstanceDeployView>>(
        error instanceof HttpErrorResponse && error.status === 404
          ? { status: 'unavailable' }
          : { status: 'error', message: serverMessage(error) ?? 'This Studio instance could not be loaded.' })),
      startWith<DeployPanel<InstanceDeployView>>({ status: 'loading' }),
    );
  }
}

function isInstanceView(value: unknown): value is InstanceDeployView {
  return !!value && typeof value === 'object' && !Array.isArray(value)
    && typeof (value as { environmentName?: unknown }).environmentName === 'string';
}
