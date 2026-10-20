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
  tenant?: string | null;
}

export interface EnvironmentStatus {
  environment: Environment;
  currentRelease: Release | null;
  currentDeployment: Deployment | null;
  newerRelease: Release | null;
  cardsSinceLastDeploy: string[];
  health: 'healthy' | 'unhealthy' | 'unknown';
  tenant?: string | null;
}
