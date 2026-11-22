export interface Environment {
  id: string;
  projectId: string;
  name: string;
  order: number;
  kind: 'staging' | 'production' | 'preview' | 'other';
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

/** State of an instance deploy run; `stalled` means no report for 30 minutes. */
export type InstanceRunState = 'running' | 'succeeded' | 'failed' | 'stalled';

export interface InstanceLastRun {
  id: string;
  state: InstanceRunState;
  version: string;
  commitSha: string;
  startedAtUtc: string;
  /** When the run ended, or its last report while it is open. */
  atUtc: string;
  /** The failed step of a failed run, else the step that reported last. */
  step: string | null;
}

/**
 * The running Studio instance behind a Production status (AGT-3949). The live
 * release itself is `currentRelease`.
 */
export interface InstanceEnvironmentFacts {
  liveSinceUtc: string | null;
  lastRun: InstanceLastRun | null;
}

export interface EnvironmentStatus {
  environment: Environment;
  currentRelease: Release | null;
  currentDeployment: Deployment | null;
  newerRelease: Release | null;
  cardsSinceLastDeploy: string[];
  health: 'healthy' | 'unhealthy' | 'unknown';
  releaseUncertain?: boolean;
  tenant?: string | null;
  instance?: InstanceEnvironmentFacts | null;
}
