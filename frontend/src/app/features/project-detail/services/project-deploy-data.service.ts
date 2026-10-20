import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, forkJoin, map, of } from 'rxjs';

export interface Environment {
  id: string;
  projectId: string;
  name: string;
  order: number;
  kind: 'staging' | 'production';
}

export interface Release {
  id: string;
  projectId: string;
  version: string;
  commitSha: string;
  artifactDigests: string[];
  processVersion: string;
  variablesVersion: string;
  cardKeys: string[];
  createdAtUtc: string;
  channel: string;
}

export type DeploymentState = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled' | 'awaiting-approval';

export interface DeploymentStep {
  name: string;
  state: DeploymentState;
  startedAtUtc: string | null;
  finishedAtUtc: string | null;
  logRef: string | null;
}

export interface Deployment {
  id: string;
  releaseId: string;
  environmentId: string;
  state: DeploymentState;
  startedAtUtc: string | null;
  finishedAtUtc: string | null;
  approvedBy: string | null;
  steps: DeploymentStep[];
}

export interface EnvironmentStatus {
  environment: Environment;
  currentRelease: Release | null;
  currentDeployment: Deployment | null;
  newerRelease: Release | null;
  cardsSinceLastDeploy: string[];
  health: 'healthy' | 'unhealthy' | 'unknown';
}

export interface PipelineStage {
  id: string;
  name: string;
  steps: string[];
  source: string;
}

export interface ProjectDeploySnapshot {
  environments: EnvironmentStatus[];
  releases: Release[];
  deployments: Deployment[];
  pipeline: PipelineStage[];
  source: 'fixture' | 'api';
}

@Injectable({ providedIn: 'root' })
export class ProjectDeployDataService {
  private readonly http = inject(HttpClient, { optional: true });

  load(projectId: string): Observable<ProjectDeploySnapshot> {
    if (this.http) {
      const root = `/api/projects/${encodeURIComponent(projectId)}/deploy`;
      return forkJoin({
        environments: this.http.get<EnvironmentStatus[]>(`${root}/status`),
        releases: this.http.get<Release[]>(`${root}/releases`),
        deployments: this.http.get<Deployment[]>(`${root}/deployments`),
      }).pipe(
        map(data => ({ ...data, pipeline: this.fixturePipeline(), source: 'api' as const })),
      );
    }
    return this.fixture(projectId);
  }

  notes(projectId: string, releaseId: string): Observable<ReleaseNotes> {
    if (!this.http) return of({ releaseId, version: '', preview: true, notes: [] });
    return this.http.get<ReleaseNotes>(`/api/projects/${encodeURIComponent(projectId)}/deploy/releases/${encodeURIComponent(releaseId)}/notes`);
  }

  private fixturePipeline(): PipelineStage[] {
    return [
      { id: 'prepare', name: 'Prepare', steps: ['commands.prepare'], source: 'project.yml' },
      { id: 'build', name: 'Build', steps: ['commands.build', 'Artifact digest'], source: 'project.yml' },
      { id: 'test', name: 'Test', steps: ['commands.test', 'commands.lint'], source: 'project.yml' },
      { id: 'release', name: 'Release', steps: ['Commit and card snapshot', 'Artifact provenance'], source: 'P1 lifecycle' },
      { id: 'staging', name: 'Staging', steps: ['Publish static bundle', 'Health check'], source: 'P1 lifecycle' },
      { id: 'production', name: 'Production', steps: ['Owner or Maintainer approval', 'Publish static bundle', 'Health check'], source: 'P1 lifecycle' },
    ];
  }

  fixture(projectId: string): Observable<ProjectDeploySnapshot> {
    const releases: Release[] = [
      { id: 'release-4', projectId, version: '2026.10.16.4', commitSha: '6b8a34d', artifactDigests: ['sha256:6b8a34d-demo-bundle'], processVersion: 'project.yml@6b8a34d', variablesVersion: 'project.yml@6b8a34d', cardKeys: ['WEB-36'], createdAtUtc: '2026-10-16T10:42:00Z', channel: 'standard' },
      { id: 'release-3', projectId, version: '2026.10.16.3', commitSha: '4f1c2e0', artifactDigests: ['sha256:4f1c2e0-demo-bundle'], processVersion: 'project.yml@4f1c2e0', variablesVersion: 'project.yml@4f1c2e0', cardKeys: ['WEB-33', 'WEB-34', 'WEB-35'], createdAtUtc: '2026-10-16T09:45:00Z', channel: 'standard' },
      { id: 'release-2', projectId, version: '2026.10.15.2', commitSha: '9c8b7a6', artifactDigests: ['sha256:9c8b7a6-demo-bundle'], processVersion: 'project.yml@9c8b7a6', variablesVersion: 'project.yml@9c8b7a6', cardKeys: ['WEB-29', 'WEB-30'], createdAtUtc: '2026-10-15T20:10:00Z', channel: 'standard' },
    ];
    const staging: Environment = { id: `${projectId}-staging`, projectId, name: 'Staging', order: 1, kind: 'staging' };
    const production: Environment = { id: `${projectId}-production`, projectId, name: 'Production', order: 2, kind: 'production' };
    const deployments: Deployment[] = [
      { id: 'deploy-stage-3', releaseId: releases[1].id, environmentId: staging.id, state: 'succeeded', startedAtUtc: '2026-10-16T10:00:00Z', finishedAtUtc: '2026-10-16T10:04:00Z', approvedBy: null, steps: [
        { name: 'Publish static bundle', state: 'succeeded', startedAtUtc: '2026-10-16T10:00:00Z', finishedAtUtc: '2026-10-16T10:03:00Z', logRef: null },
        { name: 'Health check', state: 'succeeded', startedAtUtc: '2026-10-16T10:03:00Z', finishedAtUtc: '2026-10-16T10:04:00Z', logRef: null },
      ] },
      { id: 'deploy-prod-2', releaseId: releases[2].id, environmentId: production.id, state: 'succeeded', startedAtUtc: '2026-10-15T22:10:00Z', finishedAtUtc: '2026-10-15T22:14:00Z', approvedBy: 'Owner', steps: [
        { name: 'Approval', state: 'succeeded', startedAtUtc: '2026-10-15T22:10:00Z', finishedAtUtc: '2026-10-15T22:11:00Z', logRef: null },
        { name: 'Publish static bundle', state: 'succeeded', startedAtUtc: '2026-10-15T22:11:00Z', finishedAtUtc: '2026-10-15T22:13:00Z', logRef: null },
        { name: 'Health check', state: 'succeeded', startedAtUtc: '2026-10-15T22:13:00Z', finishedAtUtc: '2026-10-15T22:14:00Z', logRef: null },
      ] },
    ];
    return of({
      environments: [
        { environment: staging, currentRelease: releases[1], currentDeployment: deployments[0], newerRelease: releases[0], cardsSinceLastDeploy: releases[0].cardKeys, health: 'unknown' },
        { environment: production, currentRelease: releases[2], currentDeployment: deployments[1], newerRelease: releases[1], cardsSinceLastDeploy: releases[1].cardKeys, health: 'unknown' },
      ],
      releases,
      deployments,
      pipeline: this.fixturePipeline(),
      source: 'fixture' as const,
    });
  }
}

export interface ReleaseNotes {
  releaseId: string;
  version: string;
  preview: boolean;
  notes: { cardKey: string; text: string }[];
}
