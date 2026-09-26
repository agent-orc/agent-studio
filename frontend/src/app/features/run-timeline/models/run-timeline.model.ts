/**
 * Cycle 9 run-timeline feature models. Lifted out of `models/job.model.ts`
 * per ADR-0034. Re-exported from the legacy file.
 *
 * RunTimeline is the per-job ordered list of CLI invocations between
 * user inputs (start / continue / recovery / restart). Backend lives
 * in backend/Services/Runner/RunTimeline.cs. lineStart / lineEnd are
 * 1-based indices into cli-output.log so the drill-down activity-log
 * filter does not have to re-derive the boundaries.
 */
import type { TaskTokenSummary } from '../../tokens';
import type { ContextUsageMetric, TaskExecutionLocation } from '../../../models/task.model';

export interface RunRecord {
  index: number;
  intent: string; // 'start' | 'continue' | 'recovery' | 'restart' | 'reissue'
  /** Durable business trigger. Missing on legacy runs and never inferred from intent. */
  trigger?: 'initial' | 'operator-continue' | 'review-finding' | 'review-concern' |
    'integration-recovery' | 'gate-failure' | 'timeout-continuation' |
    'recovery-after-crash' | 'restart' | 'replan' | 'dependency-release' | null;
  triggeredBy?: string | null;
  triggerReason?: string | null;
  triggerSource?: string | null;
  startedAt: string;
  endedAt: string | null;
  status: string; // 'running' | 'completed' | typed terminal status | 'unknown'
  /** Canonical terminal outcome when recorded or derived, such as done, failed, or superseded. */
  result?: string | null;
  /** Durable or legacy evidence used to close this projected run. */
  closeoutSource?: string | null;
  cli: string | null;
  /** Effective model resolved by the runner before this CLI process started. */
  model?: string | null;
  /** Effective thinking / reasoning level resolved for this run. */
  thinkingLevel?: string | null;
  executionLocation?: TaskExecutionLocation | null;
  exitCode: number | null;
  durationSeconds: number | null;
  inputSessionId: string | null;
  capturedSessionId: string | null;
  resumed: boolean;
  reason: string | null;
  userFollowup: string | null;
  lineStart: number | null;
  lineEnd: number | null;
  /** HEAD SHA captured immediately before the run's CLI started, or null when the project has no repo / git was unavailable. */
  headShaBefore: string | null;
  /** HEAD SHA after the run finished. Equal to headShaBefore when the agent did not commit. */
  headShaAfter: string | null;
  /**
   * Relative path (under the job folder) to the captured context this run
   * was started with. Non-null means the "Show passed context" affordance is
   * offered; the full text is fetched on demand from
   * `/api/tasks/{id}/runs/{index}/context`, never inlined in the polled list.
   */
  contextRef: string | null;
  /**
   * Optional per-run token rollup. Older backend payloads omit this; the
   * timeline renders the value only when present.
   */
  tokenSummary?: TaskTokenSummary | null;
  /**
   * Read-only snapshot of the context sources the run's CLI loaded beyond the
   * prompt (ASS-1739 / T1a). Older payloads omit it; the Execution Context
   * panel renders only when present.
   */
  executionContext?: CliExecutionContext | null;
}

/** Typed lifecycle/diagnostic event recorded by the standalone runner. */
export interface RunnerRecordedEvent {
  id: string;
  kind: 'session.started' | 'session.completed' | 'turn.started' | 'turn.completed' | 'diagnostic';
  timestamp: string;
  /**
   * Server-assigned provenance. `simulated` marks an event replayed into the
   * public demo instance from a signed fixed trace; it never represents work
   * that happened. Absent or `live` means a real runner produced it.
   */
  origin?: string | null;
  sessionId?: string | null;
  turnId?: string | null;
  runIndex?: number | null;
  cli?: string | null;
  model?: string | null;
  thinkingLevel?: string | null;
  durationMs?: number | null;
  inputTokens?: number | null;
  outputTokens?: number | null;
  reasoningTokens?: number | null;
  severity?: string | null;
  code?: string | null;
  message?: string | null;
  implementationStatus?: string | null;
  pipelineStatus?: string | null;
}

/** One context input the CLI loaded for a run. Mirrors backend `CliContextSource`. */
export interface CliContextSource {
  /** 'memory' | 'instruction-file' | 'session' | 'global-config' | 'mcp' | 'env'. */
  kind: string;
  label: string;
  path: string | null;
  exists: boolean | null;
  detail: string | null;
}

/**
 * Read-only execution-context snapshot for one run. Mirrors backend
 * `CliExecutionContext`: the scalar header (model / permission mode / cwd) plus
 * the grouped context sources the CLI loaded. `source` is 'init-frame' (parsed
 * from the CLI's own startup frame) or 'convention' (adapter + config paths).
 */
export interface CliExecutionContext {
  cli: string;
  model: string | null;
  /** Effective run-scoped reasoning level selected by qualification / RunSpec. */
  thinkingLevel?: string | null;
  permissionMode: string | null;
  cwd: string | null;
  /**
   * Resolved context mode for the run (T1b / ASS-1742): `'clean'` (isolated
   * per-run home) or `'shared'` (the operator's global state). Null for runs
   * captured before the field existed.
   */
  contextMode?: string | null;
  capturedAt: string;
  source: string;
  sources: CliContextSource[];
}

export interface RunPromptContextSnapshot {
  source: string;
  ref: string | null;
  at: string | null;
  status: string | null;
  tokenEstimate: number | null;
  metrics: ContextUsageMetric[];
}

export interface RunPromptEntry {
  index: number;
  runIndex: number;
  intent: string;
  at: string;
  label: string;
  fileName: string | null;
  promptTokenSource: string;
  promptPreview: string | null;
  promptTokenEstimate: number | null;
  contextTokenEstimate: number | null;
  contextRef: string | null;
  contextSnapshot: RunPromptContextSnapshot | null;
}

/** One operator-owned review cycle, newest first in the Runs response. */
export interface ReviewAttemptCycle {
  epoch: number;
  isCurrent: boolean;
  startedAt: string | null;
  endedAt: string | null;
  actor: string | null;
  reason: string | null;
  fromState: string | null;
  toState: string | null;
  rotatedArtifacts: number;
}

/** One read-only refinement of the original task prompt. */
export interface TaskRefinementEntry {
  id: string;
  at: string;
  actor: 'operator' | 'agent' | 'system';
  reason: string | null;
  markdown: string;
  source: 'prompt-history' | 'run-log' | 'orchestrator-history';
  runIndex: number | null;
}

/** Response of `GET /api/tasks/{id}/runs/{index}/context`. `context` is null when nothing was captured for the run. */
export interface RunContextResponse {
  runIndex: number;
  context: string | null;
  promptTokenEstimate?: number | null;
  contextTokenEstimate?: number | null;
  note?: string;
}

export interface RunTimeline {
  runCount: number;
  firstStartedAt: string | null;
  lastActivityAt: string | null;
  hasActiveRun: boolean;
  runs: RunRecord[];
  promptEntries?: RunPromptEntry[];
  /** Existing prompt/run/journal evidence folded into one chronological read model. */
  refinements?: TaskRefinementEntry[];
  /** Typed runner replay. Task-level tokenSummary remains a separate aggregate. */
  runnerEvents?: RunnerRecordedEvent[];
  /** Operator-owned anti-churn epoch. Legacy tasks are epoch zero. */
  reviewAttemptEpoch?: number;
  /** Current and closed review cycles, newest first. */
  reviewAttemptCycles?: ReviewAttemptCycle[];
}

export interface RunCommitInfo {
  sha: string;
  shortSha: string;
  authorDateUtc: string;
  author: string;
  subject: string;
  filesChanged: number;
  added: number;
  removed: number;
}

export interface RunCommitsResponse {
  runIndex: number;
  startedAt: string;
  endedAt: string | null;
  headShaBefore: string | null;
  headShaAfter: string | null;
  /** 'sha-range' (deterministic) | 'wall-clock' (fallback for older runs without captured SHAs). */
  source: 'sha-range' | 'wall-clock';
  commits: RunCommitInfo[];
}

/**
 * One row in the per-run aggregated file list. `status` is the
 * single-letter git diff filter (A/M/D/R/C). The +/- counts are the
 * combined numstat across every commit in the run that touched this
 * path. Used by the Run Git Viewer's file tree.
 */
export interface RunFileChange {
  status: string;
  path: string;
  added: number;
  removed: number;
}

export interface RunFilesResponse {
  runIndex: number;
  headShaBefore: string | null;
  headShaAfter: string | null;
  files: RunFileChange[];
  note?: string;
}

export interface RunDiffResponse {
  diff: string;
  note?: string;
}
