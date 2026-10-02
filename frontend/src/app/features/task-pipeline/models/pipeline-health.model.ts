export interface PipelineHealthAlert {
  kind: 'gate-hanging' | 'systemic-gate-failure' | 'lane-drain-stalled' | 'evidence-flush-stalled' | string;
  severity: 'high' | string;
  summary: string;
  detail: string;
  detectedAtUtc: string;
  jobId?: string | null;
  /** Git root of the workspace repository, set for `evidence-flush-stalled`. */
  repository?: string | null;
}

export interface PipelineActiveGateHealth {
  gateRunId: string;
  project: string;
  jobId: string;
  acquiredAtUtc: string;
  elapsedMinutes: number;
  budgetMinutes: number;
  isHanging: boolean;
}

export interface PipelineFingerprintHealth {
  fingerprint: string;
  consecutiveFailures: number;
  threshold: number;
  projects: string[];
  isSystemic: boolean;
}

export interface PipelineLaneDrainHealth {
  lane: string;
  queueCount: number;
  completedPerHour: number;
  oldestQueuedAtUtc?: string | null;
  isStalled: boolean;
}

export interface PipelineHealthSnapshot {
  project: string;
  capturedAtUtc: string;
  status: 'healthy' | 'running' | 'alarm';
  activeGate?: PipelineActiveGateHealth | null;
  fingerprint?: PipelineFingerprintHealth | null;
  lanes: PipelineLaneDrainHealth[];
  alerts: PipelineHealthAlert[];
}
