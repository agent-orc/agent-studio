import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { EnvironmentHealthResponse } from '../models/environment-health.model';

@Injectable({ providedIn: 'root' })
export class EnvironmentHealthService {
  private readonly http = inject(HttpClient);

  project(projectName: string) {
    return this.http.get<EnvironmentHealthResponse>(
      `/api/projects/${encodeURIComponent(projectName)}/environments/health`);
  }

  workspace() {
    return this.http.get<EnvironmentHealthResponse>('/api/environment/health');
  }
}
