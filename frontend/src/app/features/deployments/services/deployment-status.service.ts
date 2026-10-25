import { HttpClient } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import type { EnvironmentStatus } from '../models/deployment.model';

export const DEPLOYMENT_CLOCK = new InjectionToken<() => number>('deployment clock', {
  providedIn: 'root', factory: () => Date.now,
});

export interface DeployMatrixRow {
  projectId: string;
  statuses: EnvironmentStatus[];
  tenantStatuses: EnvironmentStatus[];
  error: string | null;
}

export interface DeployMatrixResponse {
  environmentKinds: string[];
  projects: DeployMatrixRow[];
}

@Injectable({ providedIn: 'root' })
export class DeploymentStatusService {
  private readonly http = inject(HttpClient);

  matrix() {
    return this.http.get<DeployMatrixResponse>('/api/deploy/matrix');
  }
}
