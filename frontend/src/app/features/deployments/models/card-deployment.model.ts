export interface CardEnvironmentBadge {
  environmentId: string;
  environmentName: string;
  kind: 'staging' | 'production' | 'other';
  state: 'not-deployed' | 'rolled-back' | 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled' | 'awaiting-approval';
  releaseId: string | null;
  releaseVersion: string | null;
  atUtc: string | null;
}

export interface CardDeploymentView {
  projectId: string;
  cardKey: string;
  environments: CardEnvironmentBadge[];
}
