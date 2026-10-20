import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** One project.yml problem; `line` is 1-based when the path resolves to a line. */
export interface PipelineDefinitionIssue {
  path: string;
  code: string;
  message: string;
  line?: number | null;
}

export interface PipelineGateNode {
  source: 'pipeline' | 'commands';
  build: string[];
  test: string[];
  testSuites: string[];
}

export interface PipelineArtifactNode {
  path: string;
  name: string;
  type: 'static' | 'container' | string;
  source?: string | null;
  image?: string | null;
}

export interface PipelineStepNode {
  path: string;
  kind: string;
  artifact?: string | null;
  target?: string | null;
  destination?: string | null;
  healthUrl?: string | null;
  healthTimeoutSeconds?: number | null;
}

export interface PipelineVariableNode {
  path: string;
  name: string;
  isSecret: boolean;
  secret?: string | null;
}

export interface PipelineEnvironmentNode {
  path: string;
  name: string;
  order: number;
  protected: boolean;
  automatic: boolean;
  approval: string[];
  requires?: string | null;
  steps: PipelineStepNode[];
  variables: PipelineVariableNode[];
}

/** Gate, then the artifacts it keeps, then each environment in lifecycle order. */
export interface PipelineDiagram {
  schemaVersion: number;
  gate: PipelineGateNode;
  artifacts: PipelineArtifactNode[];
  environments: PipelineEnvironmentNode[];
  environmentsDefaulted: boolean;
  lifecycleDefaulted: boolean;
}

export interface PipelineDefinitionResponse {
  project: string;
  path: string;
  exists: boolean;
  schemaVersion?: number | null;
  valid: boolean;
  issues: PipelineDefinitionIssue[];
  definitionSha256?: string | null;
  diagram?: PipelineDiagram | null;
  migrationAvailable: boolean;
}

/** Dry run of the schema 2 migration; nothing is written until it is confirmed. */
export interface PipelineMigrationResponse {
  project: string;
  available: boolean;
  reason?: string | null;
  baseSha256?: string | null;
  proposedYaml?: string | null;
  proposalSha256?: string | null;
  valid: boolean;
  issues: PipelineDefinitionIssue[];
  notes: string[];
  diagram?: PipelineDiagram | null;
}

export interface PipelineMigrationConfirmResponse {
  changed: boolean;
  commitSha?: string | null;
  pushQueued: boolean;
  definition: PipelineDefinitionResponse;
}

/** Typed client for `/api/projects/{project}/pipeline-definition`. */
@Injectable({ providedIn: 'root' })
export class ProjectPipelineDefinitionService {
  private readonly http = inject(HttpClient);

  read(project: string): Observable<PipelineDefinitionResponse> {
    return this.http.get<PipelineDefinitionResponse>(this.url(project));
  }

  validate(project: string, yaml: string): Observable<PipelineDefinitionResponse> {
    return this.http.post<PipelineDefinitionResponse>(`${this.url(project)}/validate`, { yaml });
  }

  dryRun(project: string): Observable<PipelineMigrationResponse> {
    return this.http.get<PipelineMigrationResponse>(`${this.url(project)}/migration`);
  }

  confirm(project: string, migration: PipelineMigrationResponse): Observable<PipelineMigrationConfirmResponse> {
    return this.http.post<PipelineMigrationConfirmResponse>(`${this.url(project)}/migration`, {
      baseSha256: migration.baseSha256,
      proposalSha256: migration.proposalSha256,
    });
  }

  private url(project: string): string {
    return `/api/projects/${encodeURIComponent(project)}/pipeline-definition`;
  }
}
