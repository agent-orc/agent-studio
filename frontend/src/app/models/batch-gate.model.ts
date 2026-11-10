export type BatchDecision = 'wait' | 'close' | 'per-task' | 'idle' | 'disabled';
export type BatchMemberStatus = 'pending' | 'in-batch' | 'published' | 'fallback' | 'pre-check-red' | 'review-fix';

export interface BatchGateSettings {
  enabled: boolean;
  scope: 'documentation' | 'code';
  planner: 'threshold' | 'look-ahead';
  minimumSize: number;
  maximumSize: number;
  deadlineMinutes: number;
  lookAheadMinutes: number;
  urgentTags: string[];
  riskPolicy: 'conservative' | 'balanced' | 'aggressive';
  /** Look-ahead risk rule: `empirical` learns from recorded batch verdicts, `legacy` keeps the fixed rule. */
  calibration: 'legacy' | 'empirical';
  /** `1` lets one formed batch gate on the previous batch's candidate (AGT-3546). */
  stacking: 'off' | '1';
  overlap: 'adjacent' | 'separate';
  reviewTestLevel: 'work-package' | 'build-only';
  gateTimeoutMinutes: number;
  preCheckEnabled: boolean;
  advisor: { mode: 'off' | 'advise' | 'enforce'; cliType: string; model?: string; timeoutSeconds: number };
}

/** The advisor mode that applies on this host; `reason` explains a configured advisor the host cannot run. */
export interface BatchGateAdvisorEffective {
  effectiveMode: 'off' | 'advise' | 'enforce';
  carrier: 'monolith' | 'engine' | 'none';
  reason: string | null;
}

export interface BatchGateConfiguration {
  settings: BatchGateSettings;
  advisorEffective?: BatchGateAdvisorEffective | null;
  limits: { hardMaximumSize: number };
}

export interface BatchGateState {
  reviewFixWaits?: { taskKey: string; reviewAttemptId: string; round: number; maximum: number; aspects: string[] }[];
  preChecksRun?: number;
  preChecksRed?: number;
  preCheckMinutesSaved?: number;
  gateSlots?: { busy: number; total: number } | null;
  gateCapacity?: { slots: number; processorsPerGate: number; hostCores: number | null;
    allowOversubscription: boolean } | null;
  pushWait?: BatchGatePushWait | null;
  pushWaits?: BatchGatePushWait[];
  plan: {
    decision: BatchDecision;
    reason: string;
    closeAtUtc?: string | null;
    proposedMembers: string[];
    targetSize: number;
    upcoming: { taskKey: string; stage: string; etaUtc?: string | null; risk: BatchRisk; overlapsWith: string[] }[];
    advisor?: { mode: string; verdict: string; reasons: string[]; model: string; atUtc: string } | null;
  };
  pending: { taskKey: string; reviewAttemptId: string; resultSha: string; waitMinutes: number; risk: BatchRisk; paths?: string[]; riskAgeMinutes?: number | null; exclusion?: string | null; preCheck?: BatchPreCheck | null }[];
  batches: {
    batchId: string;
    parentBatchId?: string | null;
    phase: string;
    phaseReason?: string | null;
    phaseHistory?: { phase: string; reason?: string | null; atUtc: string }[];
    formationReason?: string | null;
    exclusions?: { taskKey: string; reason: string; detail?: string | null }[];
    baseSha: string;
    candidateSha?: string | null;
    members: { taskKey: string; replay: BatchReplay; released: boolean }[];
    runs: { batchRunId: string; host: string; outcome?: string | null; classification?: string | null; evidenceAvailable?: boolean; startedAtUtc?: string | null; finishedAtUtc?: string | null }[];
    publication?: BatchPublication | null;
  }[];
  members: Record<string, BatchMemberState>;
  /** AGT-3577: per member of a red batch, what the bisection proved. */
  isolationFindings?: BatchIsolationFinding[];
}

export type BatchGateBatch = BatchGateState['batches'][number];

/**
 * One member's isolation outcome under the red batch `parentBatchId`. A
 * proven-red finding carries the failed tests of the child run that proved it.
 */
export interface BatchIsolationFinding {
  parentBatchId: string;
  taskKey: string;
  status: 'proven-red' | 'proven-green' | 'superseded' | string;
  evidencePath: string;
  recordedAtUtc: string;
  resultSha?: string;
  provingBatchId?: string | null;
  provingRunId?: string | null;
  failures?: { testName: string; errorExcerpt: string }[];
  evidenceAvailable?: boolean;
}

export type BatchIsolationRowStatus = 'proven-red' | 'proven-green' | 'superseded' | 'not-isolated';
export interface BatchIsolationRow {
  taskKey: string;
  status: BatchIsolationRowStatus;
  finding: BatchIsolationFinding | null;
}

/** Red or isolating batch of a project, for the lane indicator. */
export interface BatchAlert {
  state: 'red' | 'isolating';
  batchId: string;
  culprits: string[];
}

export interface BatchGatePushWait {
  reason: string;
  jobId: string;
  approvedSha: string | null;
  sinceUtc: string;
  ageAlert: boolean;
  ageMinutes: number;
}

/**
 * One card's batch status. An ejected member carries why it went to the
 * per-task gate, `conflict` or `unattributable`, with the replay detail in
 * `reasonDetail`.
 */
export interface BatchMemberState {
  status: BatchMemberStatus;
  batchId?: string | null;
  phase?: string | null;
  position?: number | null;
  size?: number | null;
  reason?: string | null;
  reasonDetail?: string | null;
  preCheck?: BatchPreCheck | null;
}

export interface BatchPreCheck {
  outcome: 'green' | 'red';
  resultSha: string;
  baseSha: string;
  gateProfileDigest: string;
  reason: string;
  failedTests: string[];
  completedAtUtc: string;
}

/** The backend reports risk as a probability between 0 and 1. */
export type BatchRisk = number | string | null;
/** The backend reports a member's replay as an object with an outcome, or null before replay. */
export type BatchReplay = { outcome?: string | null } | string | null;
export interface BatchPublication {
  verifiedRemoteSha?: string | null;
  testedCandidateSha?: string | null;
  publishedSha?: string | null;
  candidateSha?: string | null;
  atUtc?: string | null;
  [key: string]: unknown;
}

export function batchRiskLabel(risk: BatchRisk | undefined): string {
  if (typeof risk === 'number' && Number.isFinite(risk)) return `${Math.round(risk * 100)} %`;
  return typeof risk === 'string' && risk ? risk : 'unknown';
}

export function batchReplayLabel(replay: BatchReplay | undefined): string {
  if (typeof replay === 'string') return replay;
  return replay?.outcome ?? 'not replayed';
}

/** Short card label for why a member left its batch. */
export function batchEjectionLabel(reason: string | null | undefined): string | null {
  switch (reason) {
    case 'conflict': return 'replay conflict';
    case 'unattributable': return 'unattributable history';
    default: return reason || null;
  }
}

export function batchPublishedSha(publication: BatchPublication | null | undefined): string | null {
  return publication?.verifiedRemoteSha ?? publication?.publishedSha ?? null;
}

export function batchRunStatus(batch: BatchGateBatch, run: BatchGateBatch['runs'][number]): string {
  if (run.outcome) return run.outcome;
  return batch.phase === 'running' && !run.finishedAtUtc ? 'running' : 'interrupted';
}

export function batchRunEvidenceUrl(project: string, batchId: string, runId: string, view: 'evidence' | 'log' = 'evidence'): string {
  return `/api/projects/${encodeURIComponent(project)}/batch-gate/run-${view}?batchId=${encodeURIComponent(batchId)}&runId=${encodeURIComponent(runId)}`;
}

export function batchMemberFor(state: BatchGateState | null, ...keys: (string | null | undefined)[]) {
  if (!state) return null;
  for (const key of keys) {
    if (key && state.members[key]) return state.members[key];
  }
  return null;
}

/** Lifetime batch pilot figures from `GET /batch-gate/report` (subset the UI reads). */
export interface BatchGateReport {
  project: string;
  closedBatches: number;
  eligibleDeliveredMembers: number;
  fullSuiteRuns: number;
  meanBatchSize: number | null;
  batchGreenRate: number | null;
  constructionEjectionRate: number | null;
}

export interface BatchTestFailureRank {
  testName: string;
  batches: number;
  runs: number;
  distinctMemberSets: number;
  firstFailureUtc: string;
  lastFailureUtc: string;
  latestErrorExcerpt: string;
  flakyRerunPassed: boolean;
  flakySuspect: boolean;
  batchIds: string[];
  failures: { testName: string; batchId: string; runId: string; members: string[];
    atUtc: string; errorExcerpt: string; flakyRerunPassed: boolean }[];
}

export interface BatchTestFailureReport {
  project: string;
  windowDays: number;
  tests: BatchTestFailureRank[];
  pendingEvidenceRuns: number;
}

/** AGT-3219: cards parked by a machine gate queue overrun, from `/batch-gate/queue-stalls`. */
export interface GateQueueStallReport {
  project: string;
  releasable: string[];
}

/** Outcome of the one-step release of queue-stalled cards back to the batch queue. */
export interface GateQueueStallRelease {
  released: string[];
  refused: { taskKey: string; reason: string }[];
}

/**
 * True for a park reason that names the machine gate queue budget spent in
 * its queue phase: the card's gate never ran, so its code was not judged.
 */
export function isGateQueueOverrunReason(reason: string | null | undefined): boolean {
  return !!reason && reason.includes('machine-gate-queue') && reason.includes('phase=queue');
}

const ACTIVE_PHASES = new Set(['formed', 'assembling', 'assembled', 'running', 'green', 'publishing']);

function latestFindings(state: BatchGateState | null, parentBatchId: string): Map<string, BatchIsolationFinding> {
  const latest = new Map<string, BatchIsolationFinding>();
  for (const finding of state?.isolationFindings ?? []) {
    if (finding.parentBatchId !== parentBatchId) continue;
    const known = latest.get(finding.taskKey);
    if (!known || Date.parse(finding.recordedAtUtc) >= Date.parse(known.recordedAtUtc)) latest.set(finding.taskKey, finding);
  }
  return latest;
}

/**
 * AGT-3577: per member of a red parent batch, proven red, proven green,
 * superseded, or not yet isolated. Null for a batch nothing was isolated in.
 */
export function batchIsolationRows(state: BatchGateState | null, batch: BatchGateBatch): BatchIsolationRow[] | null {
  if (batch.parentBatchId) return null;
  const findings = latestFindings(state, batch.batchId);
  if (batch.phase !== 'red' && findings.size === 0) return null;
  return batch.members.map(member => {
    const finding = findings.get(member.taskKey) ?? null;
    const status = finding?.status;
    return {
      taskKey: member.taskKey, finding,
      status: status === 'proven-red' || status === 'proven-green' || status === 'superseded' ? status : 'not-isolated',
    };
  });
}

/**
 * The project's acute batch state. A red batch isolates while one of its
 * child batches is active; afterwards it stays red until a newer batch
 * publishes. History stays quiet (R4).
 */
export function batchAlert(state: BatchGateState | null): BatchAlert | null {
  const batches = state?.batches ?? [];
  const roots = batches.filter(batch => !batch.parentBatchId);
  for (const [index, batch] of roots.entries()) {
    if (batch.phase !== 'red') continue;
    const children = batches.filter(child => child.parentBatchId === batch.batchId);
    const culprits = [...latestFindings(state, batch.batchId).values()]
      .filter(finding => finding.status === 'proven-red').map(finding => finding.taskKey);
    if (children.some(child => ACTIVE_PHASES.has(child.phase))) return { state: 'isolating', batchId: batch.batchId, culprits };
    // Batches arrive newest first: a published newer batch means the project moved on.
    if (roots.slice(0, index).some(newer => newer.phase === 'published')) return null;
    return { state: 'red', batchId: batch.batchId, culprits };
  }
  return null;
}

/**
 * The current proven-red finding of a card: one under the card's batch or
 * that batch's parent, or for a card outside a batch one for its result SHA.
 * A finding about an earlier delivery does not mark a later one red.
 */
export function batchProvenRedFor(state: BatchGateState | null, member: BatchMemberState | null,
  ...keys: (string | null | undefined)[]): BatchIsolationFinding | null {
  if (!state || !member) return null;
  const ids = new Set(keys.filter((key): key is string => !!key));
  const parent = member.batchId ? state.batches.find(batch => batch.batchId === member.batchId)?.parentBatchId : null;
  const pendingSha = state.pending.find(item => ids.has(item.taskKey))?.resultSha;
  const current = (state.isolationFindings ?? []).filter(finding => ids.has(finding.taskKey)
    && (member.batchId ? finding.parentBatchId === member.batchId || finding.parentBatchId === parent
      : !!pendingSha && finding.resultSha?.toLowerCase() === pendingSha.toLowerCase()))
    .sort((a, b) => Date.parse(a.recordedAtUtc) - Date.parse(b.recordedAtUtc));
  const latest = current[current.length - 1];
  return latest?.status === 'proven-red' ? latest : null;
}

export function batchIsolationEvidenceUrl(project: string, finding: BatchIsolationFinding): string {
  return `/api/projects/${encodeURIComponent(project)}/batch-gate/isolation-evidence?parentBatchId=${
    encodeURIComponent(finding.parentBatchId)}&taskKey=${encodeURIComponent(finding.taskKey)}`;
}

/** A generated 32-character batch id reads by its first eight characters. */
export function batchShortId(batchId: string): string {
  return /^[0-9a-f]{32}$/i.test(batchId) ? batchId.slice(0, 8) : batchId;
}
