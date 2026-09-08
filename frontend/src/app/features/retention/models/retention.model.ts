export type RetentionArtifactClass = 'Authority' | 'Evidence' | 'HeavyWorkingData' | 'Runtime';

export interface RetentionRule {
  id: string;
  artifactClass: RetentionArtifactClass;
  hotCapBytesPerFile: number;
  hotBudgetBytesPerTask: number;
  refuseAboveBytes: number;
  archiveAfterDaysTerminal: number | null;
  archiveTaskAfterDaysTerminal: number | null;
  deleteAfterDays: number | null;
  deleteArchiveAfterDaysTerminal: number | null;
  deleteArchiveEnabled: boolean;
  neverArchiveLanes: string[];
}

export interface FullBackupRetention {
  daily: number;
  weekly: number;
  monthly: number;
}

export interface RetentionPolicy {
  scope: string;
  version: number;
  updatedAt: string;
  updatedBy: string;
  rules: RetentionRule[];
  fullBackups?: FullBackupRetention | null;
}

export interface UpdateRetentionPolicyRequest {
  rules: RetentionRule[];
  expectedVersion: number;
  fullBackups?: FullBackupRetention | null;
}

export interface RetentionAction {
  kind: string;
  ruleId: string;
  project: string;
  taskKey: string;
  taskId: string;
  stage: number;
  bytes: number;
  fileCount: number;
  reason: string;
  lane?: string | null;
  terminalAt?: string | null;
}

export interface RetentionPlan {
  plannedAt: string;
  policyVersion: number;
  actionCount: number;
  totalBytes: number;
  affectedTasks: number;
  actions: RetentionAction[];
}

export interface RetentionApplyResult {
  runId: string;
  plan: RetentionPlan;
  appliedActions: number;
  appliedBytes: number;
  errors: string[];
  warnings: string[];
}

export interface RetentionRunSummary {
  id: string;
  startedAt: string;
  finishedAt: string | null;
  trigger: string;
  mode: string;
  policyVersion: number;
  actionCount: number;
  appliedBytes: number;
  actorId: string;
  errorCount?: number;
  warningCount?: number;
}

export interface RetentionRunDetail {
  summary: RetentionRunSummary;
  plan: RetentionPlan;
  errors: string[];
  warnings: string[];
}

export interface RetentionSchedule {
  enabled: boolean;
  serverLocalHour: number;
  nextRunAt: string | null;
}

export interface RetentionArchiveFile {
  name: string;
  size: number;
  sha256: string;
}

export interface RetentionArchiveStage {
  stage: number;
  archivedAt: string;
  payloadPath: string;
  payloadSha256: string;
  totalBytes: number;
  files: RetentionArchiveFile[];
  policyVersion: number;
  archivedBy: string;
}

export interface RetentionArchiveManifest {
  taskId: string;
  taskKey: string;
  project: string;
  archivedAt: string;
  state: string;
  totalBytes: number;
  restoredAt: string | null;
  stages: RetentionArchiveStage[];
  tombstonedAt: string | null;
}

export interface FullBackupSummary {
  id: string;
  createdAt: string;
  totalBytes: number;
  taskCount: number;
  coldPayloadCount: number;
  setSha256: string;
  warnings: string[];
}

export interface ListFullBackupsResponse {
  backups: FullBackupSummary[];
}

export interface VerifyFullBackupResult {
  backupId: string;
  verified: boolean;
  summary: FullBackupSummary;
}

export interface RestoreFullBackupResult {
  backupId: string;
  restored: boolean;
  message: string;
}

export const MIB = 1024 * 1024;

export const PLATFORM_RETENTION_RULES: readonly RetentionRule[] = [
  {
    id: 'authority-keep', artifactClass: 'Authority', hotCapBytesPerFile: 0,
    hotBudgetBytesPerTask: 0, refuseAboveBytes: 0, archiveAfterDaysTerminal: null,
    archiveTaskAfterDaysTerminal: null, deleteAfterDays: null,
    deleteArchiveAfterDaysTerminal: null, deleteArchiveEnabled: false,
    neverArchiveLanes: [],
  },
  {
    id: 'evidence-stage-2', artifactClass: 'Evidence', hotCapBytesPerFile: 0,
    hotBudgetBytesPerTask: 0, refuseAboveBytes: 0, archiveAfterDaysTerminal: null,
    archiveTaskAfterDaysTerminal: 180, deleteAfterDays: null,
    deleteArchiveAfterDaysTerminal: null, deleteArchiveEnabled: false,
    neverArchiveLanes: [],
  },
  {
    id: 'heavy-stage-1', artifactClass: 'HeavyWorkingData', hotCapBytesPerFile: 10 * MIB,
    hotBudgetBytesPerTask: 64 * MIB, refuseAboveBytes: 50 * MIB,
    archiveAfterDaysTerminal: 30, archiveTaskAfterDaysTerminal: 180,
    deleteAfterDays: null, deleteArchiveAfterDaysTerminal: 730,
    deleteArchiveEnabled: false, neverArchiveLanes: [],
  },
  {
    id: 'runtime-delete', artifactClass: 'Runtime', hotCapBytesPerFile: 0,
    hotBudgetBytesPerTask: 0, refuseAboveBytes: 0, archiveAfterDaysTerminal: null,
    archiveTaskAfterDaysTerminal: null, deleteAfterDays: 30,
    deleteArchiveAfterDaysTerminal: null, deleteArchiveEnabled: false,
    neverArchiveLanes: [],
  },
];

export function formatRetentionBytes(bytes: number | null | undefined): string {
  if (bytes == null || !Number.isFinite(bytes)) return '–';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < MIB) return `${(bytes / 1024).toFixed(bytes < 10 * 1024 ? 1 : 0)} KiB`;
  const mib = bytes / MIB;
  if (mib < 1024) return `${mib.toFixed(mib < 10 ? 1 : 0)} MiB`;
  return `${(mib / 1024).toFixed(1)} GiB`;
}

export function sameRetentionRule(left: RetentionRule, right: RetentionRule): boolean {
  const normalize = (rule: RetentionRule) => ({
    ...rule,
    neverArchiveLanes: [...rule.neverArchiveLanes].sort(),
  });
  return JSON.stringify(normalize(left)) === JSON.stringify(normalize(right));
}
