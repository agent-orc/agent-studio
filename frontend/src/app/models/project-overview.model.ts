/** Compact project throughput used by the operator-first Project Overview. */
export interface ProjectThroughputSummary {
  project: string;
  capturedAt: string;
  completedLast24h: number;
  completedLast7d: number;
  recentCompletions: ProjectCompletionSummary[];
  gateSlots?: { busy: number; total: number } | null;
}

export interface ProjectCompletionSummary {
  taskId: string;
  taskKey: string;
  title: string;
  completedAt: string;
}

/** One commit included in, or waiting for, the stable deployment. */
export interface ProjectDeploymentCommit {
  sha: string;
  shortSha: string;
  subject: string;
  authorDateUtc: string;
}

/** Latest deploy-stable run plus the current integration-to-deployed delta. */
export interface ProjectDeploymentSummary {
  project: string;
  available: boolean;
  reason: string | null;
  source: string;
  lastDeployment: ProjectDeploymentRun | null;
  history: ProjectDeploymentRun[];
  pendingCount: number | null;
  pendingCommits: ProjectDeploymentCommit[];
  targets: ProjectDeploymentTarget[];
  defaultEvidenceRun: DeploymentTestRunReference | null;
}

export interface TestRunScope {
  level: string;
  testSet: string;
}

export interface DeploymentTestRunReference {
  id: string;
  commit: string;
  branch: string;
  scope: TestRunScope;
  completedAt: string | null;
  distanceToHead: number | null;
  headDirection: 'exact' | 'head-ahead' | 'head-behind' | 'diverged' | 'unknown';
}

export interface TestRunRecord {
  id: string;
  projectId: string;
  trigger: string;
  commit: string;
  branch: string;
  scope: TestRunScope;
  state: 'planned' | 'running' | 'completed';
  result: 'passed' | 'failed' | 'canceled' | null;
  durationSeconds: number | null;
  host: string | null;
  plannedOrder: number;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
}

export interface ProjectTestRunItem {
  run: TestRunRecord;
  attachedTasks: { taskKey: string; title: string }[];
}

export interface ProjectTestRunsResponse {
  project: string;
  headCommit: string | null;
  runs: ProjectTestRunItem[];
}

export type ProjectDeploymentParameterType = 'string' | 'boolean' | 'branch' | 'enum' | 'secret-ref';

export interface ProjectDeploymentParameter {
  name: string;
  type: ProjectDeploymentParameterType;
  required: boolean;
  default: unknown;
  options: string[];
}

export interface ProjectDeploymentTarget {
  id: string;
  title: string;
  kind: 'derived' | 'template' | 'prompt';
  template: string | null;
  summary: string;
  runnable: boolean;
  source: string;
  command: string | null;
  targetHostId: string | null;
  parameters: ProjectDeploymentParameter[];
}

export interface CompiledDeploymentPrompt {
  title: string;
  summary: string;
  command: string | null;
  parameters: ProjectDeploymentParameter[];
  warnings: string[];
  runnable: boolean;
}

export interface ProjectDeploymentRun {
    at: string;
    status: string;
    headBefore: string;
    headAfter: string;
    durationSeconds: number;
    jobsSinceLastRestart: number;
    reviewCountAfter: number;
    commits: ProjectDeploymentCommit[];
}

export type ProjectVisualEvidenceReviewStatus = 'unseen' | 'reviewed' | 'unavailable';

export interface ProjectVisualEvidenceItem {
  id: string;
  jobId: string;
  jobTitle: string;
  watchPath: string;
  fileName: string;
  relativePath: string;
  url: string | null;
  caption: string;
  testStatus: string | null;
  source: string;
  capturedAt: string;
  reviewStatus: ProjectVisualEvidenceReviewStatus;
}

export interface ProjectVisualEvidenceQueue {
  project: string;
  capturedAt: string;
  unseenCount: number;
  items: ProjectVisualEvidenceItem[];
}
