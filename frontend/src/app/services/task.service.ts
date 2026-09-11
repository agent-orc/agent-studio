import { Injectable, signal, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { catchError, finalize, map } from 'rxjs';
import type {
  ArchivedTasksResponse,
  BatchMoveItemInput,
  BatchMoveJobResponse,
  CreateTaskRequest,
  GroupedJobs,
  GroupedJobsResponse,
  TaskArtifactsResponse,
  TaskFileHistoryEntry,
  ResultHistoryDocument,
  ResultHistoryEntry,
  TaskFileSourceScope,
  TaskDetail,
  TaskInfo,
  FileGenerationMeta,
  WatchPathEntry,
  RegistryWorkspaceListItem,
  CrashRecoveryActionResult,
  CrashRecoveryPending,
  CliOutputLine,
  RunnerStatus,
  CliSettings,
  TaskOrderItem,
  ContextUsageSnapshot,
  CliType,
  ContinueMode,
  ContinueTaskResponse,
  ProjectSnapshot,
  PromoteToCodingResponse,
  CreateRegistryProjectRequest,
  RegistryProjectSummary,
  ProjectUrlDiagnostic,
  ProjectUrlStartRule,
  ProjectUrlSuggestion,
  ProjectUrlProcessSnapshot,
  PublishActionPanel,
  PublishAutomationMode,
  PublishWorkflowRun,
  ReviewProjectionView,
} from '../models/task.model';
import { TaskState } from '../models/task.model';
import type { ClaudeSessionResponse } from '../features/claude';
import type { CliModelCatalog, CliCompletionContract, CliUsageReport, CliSessionDetail, CliSessionDeleteResult, CliWorkingMemoryReport, CliWorkingMemoryDeleteResult } from '../features/cli';
import type { GitFileChange, GitStatus, TaskCommitDetail, TaskProvenanceView } from '../features/git';
import type {
  OrchestratorLogResponse,
  OrchestratorSessionResponse,
  OrchestratorContextDigest,
  OrchestratorChatResponse,
  OrchestratorChatTurn,
} from '../features/orchestrator';
import type {
  ProjectTokenUsageSummary,
  ProjectTokenHeatmap,
  ProjectExpensiveJobsResponse,
  ProjectJobTokenDetail,
  ProjectPipelineCostTimeline,
} from '../features/project-token-usage';
import type {
  RunTimeline,
  RunCommitsResponse,
  RunFilesResponse,
  RunDiffResponse,
  RunContextResponse,
} from '../features/run-timeline';
import type { TaskTimelineEvent } from '../features/task-timeline';
import type {
  TaskPipelineResponse,
  PipelineCatalogue,
  PipelineStepProbeResult,
  PipelineStepSetting,
  PipelineStepCondition,
  StepPromptsResponse,
  PipelineHealthSnapshot,
} from '../features/task-pipeline';
import type { TaskScreenshotsResponse, WorkspaceScreenshotsResponse } from '../features/screenshots';
import type { ExecutiveSummaryResponse } from '../features/summary';
import type {
  AgentWorkSummary,
  AgentWorkDetail,
  SessionEventsResponse,
} from '../features/session-events';
import type { TaskPlanView } from '../features/plan-strip/plan.model';
import type { RegressionRadarResult } from '../features/regression-radar';
import { ErrorDialogService } from './error-dialog.service';
import { JobsHubClient } from './jobs-hub-client.service';
import type {
  ProjectDeploymentSummary,
  ProjectTestRunsResponse,
  CompiledDeploymentPrompt,
  ProjectThroughputSummary,
  ProjectVisualEvidenceItem,
  ProjectVisualEvidenceQueue,
} from '../models/project-overview.model';

/** One row in the code-review list endpoint response (see backend `CodeReviewListEntry`). */
export interface CodeReviewListEntry {
  fileName: string;
  verdict: string;
  /**
   * Quality grade `A`/`B`/`C`/`D` from the automatic grade pass; null for the
   * older user-triggered verdict reviews that carry no grade. Mirrors backend
   * `CodeReviewListEntry.Grade` (already serialised over the wire).
   */
  grade?: string | null;
  summary: string;
  model: string;
  cliType: string;
  thinkingLevel?: string | null;
  commit?: string | null;
  runAt: string;
  inputTokens?: number;
  outputTokens?: number;
  cacheReadTokens?: number;
  cacheCreationTokens?: number;
  totalTokens?: number;
  estimatedApiCostUsd?: number;
  priceKnown?: boolean;
  generation?: FileGenerationMeta | null;
  councilReaction?: CouncilReviewReaction | null;
}

export interface CouncilFindingAssessment {
  finding: string;
  action: 'FixNextRound' | 'Accept' | 'Escalate';
  reason: string;
}

export interface CouncilReviewReaction {
  createdAt: string;
  reviewFileName: string;
  grade: string;
  disposition: 'Accept' | 'Reissue' | 'Escalate';
  summary: string;
  assessments: CouncilFindingAssessment[];
  startsNewRound: boolean;
  targetJobId?: string | null;
  targetRunAttempt?: number | null;
}

/**
 * Reply from `GET /api/tasks/{id}/git/file` (and the commit-scoped variant).
 * `content` is the file's UTF-8 text; `isBinary` is true for a NUL-containing
 * blob, in which case `content` is empty and the pane declines to preview it.
 */
export interface GitFileContentResponse {
  content: string;
  isBinary: boolean;
}

/** Reply from `GET /api/tasks/code-review/defaults` (see backend `CodeReviewDefaultsResponse`). */
export interface CodeReviewDefaults {
  cliType: string;
  model: string;
}

/** Reply from `POST /api/tasks/{id}/code-review` (see backend `CodeReviewStepEndpointResponse`). */
export interface CodeReviewRunResponse {
  fileName: string;
  verdict: string;
  summary: string;
  model: string;
  cliType: string;
  thinkingLevel?: string | null;
  commit?: string | null;
  concernTagId?: string | null;
  durationMs: number;
  startedAt: string;
  grade?: string | null;
}

/** Reply from the accepted-delivery integration recovery action. */
export interface IntegrationRecoveryResponse {
  status: 'queued';
  mode: 'steer';
  targetState: string;
  position: number;
  deliveryRef: string;
  resultSha: string;
  integrationBranch: string;
}

type LaneKey = keyof GroupedJobs;
// ADR-0025: state strings use the new seven-lane order.
// ADR-0026: 1a-orchestrator-prep joins the catalog. The 1b-needs-human-review
// bounce lane has been retired (its "Human decision needed" concept was
// obsoleted; the backend boot-migrates stray 1b folders to 2-ready).
// ADR-0051 drain-era plumbing: 3a-failed-pickup is retired (no live path
// populates it, board no longer renders it). The mapping stays so a
// historical folder still parses into its group while the boot drain empties
// the lane.
const STATE_TO_LANE: Record<string, LaneKey> = {
  [TaskState.Backlog]: 'backlog',
  [TaskState.Preparation]: 'preparation',
  [TaskState.OrchestratorPrep]: 'orchestratorPrep',
  [TaskState.Ready]: 'ready',
  [TaskState.Progress]: 'progress',
  [TaskState.FailedPickup]: 'failedPickup',
  [TaskState.CodeNotComplete]: 'codeNotComplete',
  [TaskState.AutoReview]: 'autoReview',
  [TaskState.HumanReview]: 'humanReview',
  [TaskState.Escalated]: 'escalated',
  [TaskState.Completed]: 'completed',
  [TaskState.Archive]: 'archive',
};

/**
 * The grouped response already contains every live board task. Build the flat
 * signal from that same snapshot so one refresh cannot launch two equivalent
 * backend enrichment pipelines. The legacy `review` lane aliases
 * `autoReview`, hence the identity-based de-duplication.
 */
function uniqueJobsFromGrouped(grouped: GroupedJobs): TaskInfo[] {
  const seen = new Set<string>();
  const jobs: TaskInfo[] = [];
  for (const lane of Object.values(grouped)) {
    for (const job of lane ?? []) {
      const key = job.taskKey || `${job.watchPath}::${job.id}`;
      if (seen.has(key)) continue;
      seen.add(key);
      jobs.push(job);
    }
  }
  return jobs;
}

/**
 * Turn an orchestrator context key (`project:<PROJ>` or `task:<PROJ>/<KEY>`,
 * mirroring the backend `OrchestratorContextKey`) into the URL path segment(s)
 * for the `/api/runner/{contextKey}/orchestrator-chat` route. Each id part is
 * URL-encoded on its own so a project name with spaces stays valid while the
 * literal `task:`/`project:` prefix and the `<proj>/<key>` slash — which the
 * backend routes match structurally — are preserved. Unrecognized shapes fall
 * back to encoding the whole key as one segment.
 */
export function orchestratorContextChatSegment(contextKey: string): string {
  const taskPrefix = 'task:';
  const projectPrefix = 'project:';
  if (contextKey.startsWith(taskPrefix)) {
    const rest = contextKey.slice(taskPrefix.length);
    const slash = rest.indexOf('/');
    if (slash >= 0) {
      const proj = rest.slice(0, slash);
      const key = rest.slice(slash + 1);
      return `task:${encodeURIComponent(proj)}/${encodeURIComponent(key)}`;
    }
  } else if (contextKey.startsWith(projectPrefix)) {
    return `project:${encodeURIComponent(contextKey.slice(projectPrefix.length))}`;
  }
  return encodeURIComponent(contextKey);
}

@Injectable({ providedIn: 'root' })
export class TaskService {
  private http = inject(HttpClient);
  private errorDialog = inject(ErrorDialogService);
  private jobsHub = inject(JobsHubClient);

  private readonly baseUrl = '/api';
  private liveUpdateTimer: ReturnType<typeof setInterval> | null = null;
  private pushRefreshHandle: ReturnType<typeof setTimeout> | null = null;
  private groupedRefreshInFlight = false;
  private groupedRefreshQueued = false;
  private groupedRefreshQueuedSilent = true;
  private runnerRefreshInFlight = false;
  private runnerRefreshQueued = false;
  private runnerRefreshQueuedSilent = true;

  // Push (SignalR `/hubs/jobs`) is the primary update path. The poll is
  // demoted to a slow heartbeat that reconciles drift and backs up the socket
  // when it is down — 30 s keeps server load low while staying inside the
  // documented 30-60 s fallback window.
  private static readonly HEARTBEAT_MS = 30000;
  // A single mutation can fan out a burst of pushes (a bulk reorder, or a
  // move that also re-stamps siblings). Coalesce them into one silent re-pull,
  // still far under the 1 s cross-tab budget.
  private static readonly PUSH_DEBOUNCE_MS = 120;

  /** True while the job-events socket is connected (diagnostics / e2e). */
  readonly pushConnected = this.jobsHub.connected;

  /**
   * AGT-2726 — the background Git-index freshness stamp folded into the last
   * `/api/tasks/grouped` response: the oldest `gitStateAt` among the
   * repositories represented on the board, and whether any of them is
   * currently unindexed or mid-refresh. The board shows this quietly next to
   * the lanes; it never gates rendering on it.
   */
  readonly gitStateAt = signal<string | null>(null);
  readonly gitStateStale = signal(false);

  // Eventual-consistency layer for drag/drop. The user-visible reorder
  // happens locally before the backend confirms (the previous round-trip
  // wait felt laggy and made consecutive drags clobber each other when a
  // silent poll resolved between drops). Three guards work together:
  //
  // - `mutationVersion` bumps on every optimistic edit. Silent polls
  //   captured the version at request start; if it has changed by the
  //   time the response arrives, we discard the response.
  // - `pendingPersistCount` counts in-flight POSTs (reorder/move). While
  //   non-zero, silent polls are rejected unconditionally — the backend
  //   has not yet seen the user's last action, so anything it returns is
  //   stale relative to the optimistic UI.
  // - `pendingGroupedSuppressUntil` extends the rejection window past the
  //   last POST response so the on-disk rewrite (`job.json` files) has
  //   time to materialise into the next /api/tasks/grouped snapshot.
  private mutationVersion = 0;
  private pendingPersistCount = 0;
  private pendingGroupedSuppressUntil = 0;
  private static readonly OPTIMISTIC_GRACE_MS = 1500;

  /** Caller invokes when sending a reorder/move POST, then again when the POST resolves. */
  beginOptimisticPersist(): void {
    this.pendingPersistCount++;
  }
  endOptimisticPersist(): void {
    if (this.pendingPersistCount > 0) this.pendingPersistCount--;
    this.pendingGroupedSuppressUntil = Date.now() + TaskService.OPTIMISTIC_GRACE_MS;
  }

  readonly jobs = signal<TaskInfo[]>([]);
  readonly grouped = signal<GroupedJobs>({
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    review: [],
    autoReview: [],
    humanReview: [],
    escalated: [],
    completed: [],
    archive: [],
  });
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly runnerStatus = signal<RunnerStatus>({ projects: {} });

  /**
   * F35: resolved per-lane sort strategy for every project, keyed
   * `projectName -> laneKey -> strategyId`. The board reads this to render
   * the lane-header indicator and to gate drag-reorder (manual only).
   * Refreshed on a slow cadence — sort strategy is a rarely-changed
   * project setting, so polling it at the 2 s board cadence would be waste.
   */
  readonly laneSortStrategies = signal<Record<string, Record<string, string>>>({});
  /** Explorer projection of the build-profile admission gate. */
  readonly projectPickupGates = signal<Record<string, {
    pickupAllowed: boolean;
    buildProfileStatus: string | null;
  }>>({});
  private laneSortStrategyTick = 0;

  /** Re-read slow-moving project settings used by the board and Explorer. */
  refreshLaneSortStrategies(): void {
    this.getAllProjectSettings().subscribe({
      next: (all) => {
        const map: Record<string, Record<string, string>> = {};
        const pickupGates: Record<string, {
          pickupAllowed: boolean;
          buildProfileStatus: string | null;
        }> = {};
        for (const [project, s] of Object.entries(all)) {
          if (s.laneSortStrategies) map[project] = s.laneSortStrategies;
          pickupGates[project] = {
            pickupAllowed: s.buildProfilePickupAllowed !== false,
            buildProfileStatus: s.buildProfile?.status ?? null,
          };
        }
        this.laneSortStrategies.set(map);
        this.projectPickupGates.set(pickupGates);
      },
      // A settings-fetch failure must not surface a dialog — the board still
      // renders fine without strategy indicators; they just fall back.
      error: () => undefined,
    });
  }

  refresh(silent = false): void {
    if (!silent) {
      this.loading.set(true);
      this.error.set(null);
    }

    this.refreshGrouped(silent);
    this.refreshRunnerStatus(silent);
  }

  /**
   * Keep the expensive board snapshot single-flight. Runner and SignalR
   * events can request another refresh while a slow response is outstanding;
   * collapse all of them into one trailing read instead of allowing an
   * unbounded queue of full snapshots to build up behind the backend.
   */
  private refreshGrouped(silent: boolean): void {
    if (this.groupedRefreshInFlight) {
      this.groupedRefreshQueued = true;
      this.groupedRefreshQueuedSilent &&= silent;
      return;
    }

    this.groupedRefreshInFlight = true;

    const versionAtStart = this.mutationVersion;
    const acceptOptimisticTarget = () => {
      if (!silent) return true;
      if (this.mutationVersion !== versionAtStart) return false;
      if (this.pendingPersistCount > 0) return false;
      if (Date.now() < this.pendingGroupedSuppressUntil) return false;
      return true;
    };

    this.http.get<GroupedJobsResponse>(`${this.baseUrl}/tasks/grouped`).pipe(
      finalize(() => this.finishGroupedRefresh()),
    ).subscribe({
      next: ({ gitStateAt, stale, ...grouped }) => {
        if (acceptOptimisticTarget()) {
          this.grouped.set(grouped);
          this.jobs.set(uniqueJobsFromGrouped(grouped));
          this.gitStateAt.set(gitStateAt);
          this.gitStateStale.set(stale);
        }
        if (silent) {
          this.error.set(null);
        }
      },
      error: (err) => {
        const message =
          err.status === 0
            ? 'Backend not reachable — is the API running on localhost:5030?'
            : err.error?.error || err.message || 'Failed to load jobs';

        this.error.set(message);
        if (!silent) {
          this.errorDialog.show(err, {
            title: 'Failed to load jobs',
            fallbackMessage: 'Failed to load jobs',
            source: 'Board refresh',
          });
        }
      },
    });
  }

  private finishGroupedRefresh(): void {
    this.groupedRefreshInFlight = false;
    if (!this.groupedRefreshQueued) {
      this.loading.set(false);
      return;
    }

    const silent = this.groupedRefreshQueuedSilent;
    this.groupedRefreshQueued = false;
    this.groupedRefreshQueuedSilent = true;
    this.loading.set(!silent);
    this.refreshGrouped(silent);
  }

  /**
   * Find the 0-based position of a job within the lane that currently
   * owns it in the local `grouped` snapshot. Returns -1 when the job is
   * not in any known lane (e.g. an external reshuffle dropped it between
   * the caller's read and this lookup). Used by the undo flow to capture
   * "where did this card sit before I moved it" so the revert can put it
   * back at the same slot via `moveJob(..., targetIndex)`.
   */
  findLaneIndex(jobId: string, watchPath: string, state: string): number {
    const lane = STATE_TO_LANE[state];
    if (!lane) return -1;
    const list = this.grouped()[lane] ?? [];
    return list.findIndex((j) => j.id === jobId && j.watchPath === watchPath);
  }

  /**
   * Apply a within-lane reorder to the local `grouped` signal immediately,
   * before the backend confirms. Idempotent: missing keys are dropped and
   * the existing lane order is preserved for any job not present in the
   * supplied list. Returns the previous lane snapshot so callers can roll
   * back on a failed POST.
   */
  applyOptimisticReorder(
    state: string,
    orderedKeys: { jobId: string; watchPath: string }[],
  ): TaskInfo[] | null {
    const lane = STATE_TO_LANE[state];
    if (!lane) return null;
    const current = this.grouped();
    const before = current[lane] ?? [];
    const byKey = new Map(before.map((j) => [`${j.watchPath}::${j.id}`, j]));
    const reordered: TaskInfo[] = [];
    const seen = new Set<string>();
    for (const k of orderedKeys) {
      const key = `${k.watchPath}::${k.jobId}`;
      const job = byKey.get(key);
      if (job && !seen.has(key)) {
        reordered.push(job);
        seen.add(key);
      }
    }
    // Preserve any lane members the caller didn't mention (defensive — e2e
    // happens in narrow trios, but production lanes can drift).
    for (const j of before) {
      const key = `${j.watchPath}::${j.id}`;
      if (!seen.has(key)) reordered.push(j);
    }
    this.grouped.set({ ...current, [lane]: reordered });
    this.mutationVersion++;
    this.pendingGroupedSuppressUntil = Date.now() + TaskService.OPTIMISTIC_GRACE_MS;
    return before;
  }

  /**
   * Apply a cross-lane move to the local `grouped` signal immediately.
   * When `insertAt` is provided, the card lands at that 0-based slot in
   * the target lane; otherwise it appends at the bottom. The slot path
   * matches the drag-and-drop drop-position contract: the backend
   * rewrites every sibling's `order` field so the resulting position is
   * stable across silent polls.
   */
  applyOptimisticMove(
    jobId: string,
    watchPath: string,
    targetState: string,
    insertAt?: number,
  ): { fromLane: LaneKey; before: TaskInfo[]; toLane: LaneKey; toBefore: TaskInfo[] } | null {
    const toLane = STATE_TO_LANE[targetState];
    if (!toLane) return null;
    const current = this.grouped();
    const key = `${watchPath}::${jobId}`;
    let fromLane: LaneKey | null = null;
    let moving: TaskInfo | null = null;
    for (const k of Object.keys(current) as LaneKey[]) {
      const found = (current[k] ?? []).find((j) => `${j.watchPath}::${j.id}` === key);
      if (found) {
        fromLane = k;
        moving = found;
        break;
      }
    }
    if (!fromLane || !moving) return null;
    // Same-lane "move" is a no-op at the data layer: the previous shape
    // filtered the card out of `fromLane` and then aliased `toLane` to the
    // already-filtered array, so the card vanished from its lane until the
    // next poll repainted. Bail out here so the column-level drop handler
    // (which used to fall into this path when a card was released over a
    // sibling card instead of a drop-zone) cannot make the card disappear,
    // even if a future caller forgets the sourceState guard.
    if (fromLane === toLane) return null;
    const fromBefore = current[fromLane] ?? [];
    const toBefore = current[toLane] ?? [];
    const next: GroupedJobs = { ...current };
    next[fromLane] = fromBefore.filter((j) => `${j.watchPath}::${j.id}` !== key);
    const movedCard = { ...moving, state: targetState };
    if (typeof insertAt === 'number') {
      const slot = Math.max(0, Math.min(insertAt, toBefore.length));
      next[toLane] = [...toBefore.slice(0, slot), movedCard, ...toBefore.slice(slot)];
    } else {
      next[toLane] = [...toBefore, movedCard];
    }
    this.grouped.set(next);
    this.mutationVersion++;
    this.pendingGroupedSuppressUntil = Date.now() + TaskService.OPTIMISTIC_GRACE_MS;
    return { fromLane, before: fromBefore, toLane, toBefore };
  }

  /** Roll back a failed optimistic reorder to the captured snapshot. */
  revertOptimisticReorder(state: string, before: TaskInfo[]): void {
    const lane = STATE_TO_LANE[state];
    if (!lane) return;
    const current = this.grouped();
    this.grouped.set({ ...current, [lane]: before });
    this.mutationVersion++;
    this.pendingGroupedSuppressUntil = 0;
  }

  /** Roll back a failed optimistic cross-lane move. */
  revertOptimisticMove(snapshot: {
    fromLane: LaneKey;
    before: TaskInfo[];
    toLane: LaneKey;
    toBefore: TaskInfo[];
  }): void {
    const current = this.grouped();
    const next: GroupedJobs = { ...current };
    next[snapshot.fromLane] = snapshot.before;
    if (snapshot.toLane !== snapshot.fromLane) next[snapshot.toLane] = snapshot.toBefore;
    this.grouped.set(next);
    this.mutationVersion++;
    this.pendingGroupedSuppressUntil = 0;
  }

  private withWatchPath(watchPath?: string): { params?: HttpParams } {
    return watchPath ? { params: new HttpParams().set('watchPath', watchPath) } : {};
  }

  private withWatchPathAndPath(watchPath: string | undefined, path: string): { params: HttpParams } {
    const base = this.withWatchPath(watchPath);
    return { params: (base.params ?? new HttpParams()).set('path', path) };
  }

  private withFileSourceParams(
    watchPath: string | undefined,
    scope: TaskFileSourceScope | undefined,
    extra?: Record<string, string | undefined>,
  ): { params?: HttpParams } {
    let params = this.withWatchPath(watchPath).params ?? new HttpParams();
    if (scope && scope !== 'auto') params = params.set('scope', scope);
    for (const [key, value] of Object.entries(extra ?? {})) {
      if (value) params = params.set(key, value);
    }
    return params.keys().length ? { params } : {};
  }

  private encodeTaskFilePath(path: string): string {
    return path
      .replace(/\\/g, '/')
      .split('/')
      .filter(Boolean)
      .map((segment) => encodeURIComponent(segment))
      .join('/');
  }

  private getUtf8Text(url: string, opts: { params?: HttpParams } = {}) {
    return this.http.get(url, { ...opts, responseType: 'arraybuffer' as const }).pipe(
      map((buffer) => new TextDecoder('utf-8').decode(buffer)),
    );
  }

  getDetail(jobId: string, watchPath?: string, project?: string) {
    let params = this.withWatchPath(watchPath).params ?? new HttpParams();
    if (project) params = params.set('project', project);
    return this.http.get<TaskDetail>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}`,
      params.keys().length ? { params } : {},
    );
  }

  /**
   * Resolve a task through the registry-backed project handle. The watch-path
   * request is retained only as a compatibility fallback while callers migrate
   * away from filesystem-addressed task lookups.
   */
  getDetailByProject(jobId: string, project: string, fallbackWatchPath?: string) {
    const handle = project.trim();
    if (!handle) return this.getDetail(jobId, fallbackWatchPath);

    const request = this.http.get<TaskDetail>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}`,
      { params: new HttpParams().set('project', handle) },
    );
    return fallbackWatchPath
      ? request.pipe(catchError(() => this.getDetail(jobId, fallbackWatchPath)))
      : request;
  }

  updateState(jobId: string, state: string, watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/state`,
      { targetState: state },
      this.withWatchPath(watchPath),
    );
  }

  moveJob(
    jobId: string,
    targetState: string,
    watchPath?: string,
    targetIndex?: number,
    reason?: string,
    operatorOverride = false,
  ) {
    const body: {
      targetState: string;
      targetIndex?: number;
      reason?: string;
      operatorOverride?: boolean;
    } = { targetState };
    if (typeof targetIndex === 'number') body.targetIndex = targetIndex;
    if (reason?.trim()) body.reason = reason.trim();
    if (operatorOverride) body.operatorOverride = true;
    return this.http.post(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/move`,
      body,
      this.withWatchPath(watchPath),
    );
  }

  /** Queue independent task moves and return the server-side job handle. */
  startBatchMove(items: readonly BatchMoveItemInput[]) {
    return this.http.post<BatchMoveJobResponse>(
      `${this.baseUrl}/tasks/batch-move`,
      { items },
    );
  }

  /** Read the latest per-item progress for a queued batch move. */
  getBatchMove(batchId: string) {
    return this.http.get<BatchMoveJobResponse>(
      `${this.baseUrl}/tasks/batch-move/${encodeURIComponent(batchId)}`,
    );
  }

  /**
   * Queue a focused steer round after an accepted remote delivery conflicted
   * with the integration branch. The backend validates the recorded conflict
   * and fenced delivery before moving the task back to Ready.
   */
  queueIntegrationRecovery(jobId: string, watchPath?: string) {
    return this.http.post<IntegrationRecoveryResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/integration/rebase`,
      null,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * ASS-1727 — page the terminal Archive lane. The board `grouped.archive`
   * is intentionally empty (the cache-backed board scan excludes the archive
   * partition), so the Archive view reads here instead. Returns a slim,
   * newest-first slice plus the full unpaged `total` so the caller can drive
   * "load more" and an accurate empty state. No full disk walk per call —
   * the backend serves this from the same cached scan that feeds the board.
   */
  getArchivedTasks(opts: { project?: string; watchPath?: string; offset?: number; limit?: number; search?: string } = {}) {
    let params = this.withWatchPath(opts.watchPath).params ?? new HttpParams();
    if (opts.project?.trim()) params = params.set('project', opts.project.trim());
    if (typeof opts.offset === 'number') params = params.set('offset', String(opts.offset));
    if (typeof opts.limit === 'number') params = params.set('limit', String(opts.limit));
    const term = opts.search?.trim();
    if (term) params = params.set('search', term);
    return this.http.get<ArchivedTasksResponse>(`${this.baseUrl}/tasks/archive`, { params });
  }

  getWatchPaths() {
    return this.http.get<WatchPathEntry[]>(`${this.baseUrl}/watch-paths`);
  }

  /**
   * F45a / ADR-0042 — list workspaces with their embedded projects.
   * Pass `includeArchived: true` to surface archived projects too
   * (default omits them).
   */
  getRegistryWorkspaces(opts?: { includeArchived?: boolean }) {
    const params = opts?.includeArchived
      ? new HttpParams().set('includeArchived', 'true')
      : undefined;
    return this.http.get<RegistryWorkspaceListItem[]>(`${this.baseUrl}/workspaces`, { params });
  }

  /**
   * Flat registry projection. Unlike GET /workspaces, this also includes
   * projects whose workspaceId is empty or no longer resolves to a real
   * workspace, so the UI can offer a recovery move for them.
   */
  getRegistryProjects(opts?: { includeArchived?: boolean }) {
    const params = opts?.includeArchived
      ? new HttpParams().set('includeArchived', 'true')
      : undefined;
    return this.http.get<RegistryProjectSummary[]>(`${this.baseUrl}/projects`, { params });
  }

  /** F45b — create a workspace. Returns the new record. */
  createRegistryWorkspace(displayName: string, color?: string | null) {
    return this.http.post<{ id: string; displayName: string }>(
      `${this.baseUrl}/workspaces`, { displayName, color: color ?? null });
  }

  /** F45b — patch a workspace (rename / color edit). */
  updateRegistryWorkspace(id: string, patch: { displayName?: string; color?: string | null; clearColor?: boolean }) {
    return this.http.put(`${this.baseUrl}/workspaces/${encodeURIComponent(id)}`, patch);
  }

  /** F45b — reorder a workspace one slot up or down. */
  reorderRegistryWorkspace(id: string, direction: -1 | 1) {
    return this.http.post(`${this.baseUrl}/workspaces/${encodeURIComponent(id)}/reorder`, { direction });
  }

  /**
   * F66 — delete a workspace. The backend refuses the default workspace (409)
   * and also refuses (409) any non-default workspace that still has projects
   * assigned, with a "move all N projects out first" message in the error
   * body. Projects are never auto-moved; the operator empties the workspace,
   * then it is deletable.
   */
  deleteRegistryWorkspace(id: string) {
    return this.http.delete<{ deletedId: string }>(
      `${this.baseUrl}/workspaces/${encodeURIComponent(id)}`);
  }

  /** F45b — patch a project record (rename / short-code / color / workspace / archived / paths). */
  updateRegistryProject(projId: string, patch: {
    displayName?: string;
    shortCode?: string;
    color?: string | null;
    clearColor?: boolean;
    workspaceId?: string;
    archived?: boolean;
    repositoryPath?: string;
    clearRepositoryPath?: boolean;
    rootPath?: string;
    clearRootPath?: boolean;
    repositoryUrl?: string;
    clearRepositoryUrl?: boolean;
    cliDefault?: CliType;
    clearCliDefault?: boolean;
    modelDefault?: string;
    clearModelDefault?: boolean;
    executionRunner?: string;
    clearExecutionRunner?: boolean;
  }) {
    return this.http.put<RegistryProjectSummary>(`${this.baseUrl}/projects/${encodeURIComponent(projId)}`, patch);
  }

  /** Create a registry project. Backend chooses projects/PROJ-NNN/tasks; no storage path is accepted from the UI. */
  createRegistryProject(body: CreateRegistryProjectRequest) {
    return this.http.post<RegistryProjectSummary>(`${this.baseUrl}/projects`, body);
  }

  // ----- Project URLs (per-project watchable dev-server / preview URLs) -----

  /** Detected URL suggestions from the project's repo (package.json / angular.json / README). */
  getProjectUrlSuggestions(projId: string) {
    return this.http.get<ProjectUrlSuggestion[]>(
      `${this.baseUrl}/projects/${encodeURIComponent(projId)}/url-suggestions`);
  }

  /** Add a URL to the project. Returns the updated project record. */
  addProjectUrl(projId: string, body: { label: string; url: string; startRule?: ProjectUrlStartRule | null }) {
    return this.http.post<RegistryProjectSummary>(
      `${this.baseUrl}/projects/${encodeURIComponent(projId)}/urls`, body);
  }

  /** Update an existing URL (label / url / start rule). */
  updateProjectUrl(projId: string, urlId: string, body: { label: string; url: string; startRule?: ProjectUrlStartRule | null }) {
    return this.http.put<RegistryProjectSummary>(
      `${this.baseUrl}/projects/${encodeURIComponent(projId)}/urls/${encodeURIComponent(urlId)}`, body);
  }

  /** Remove a URL by id. Returns the updated project record. */
  removeProjectUrl(projId: string, urlId: string) {
    return this.http.delete<RegistryProjectSummary>(
      `${this.baseUrl}/projects/${encodeURIComponent(projId)}/urls/${encodeURIComponent(urlId)}`);
  }

  /** Start/restart the owned dev server and return its observable session. */
  startProjectUrl(projId: string, urlId: string) {
    return this.http.post<ProjectUrlProcessSnapshot>(
      `${this.baseUrl}/projects/${encodeURIComponent(projId)}/urls/${encodeURIComponent(urlId)}/start`, {});
  }

  /** Current owned process, or null (HTTP 204) when Studio did not start one. */
  getProjectUrlProcess(projId: string, urlId: string) {
    return this.http.get<ProjectUrlProcessSnapshot | null>(
      `${this.baseUrl}/projects/${encodeURIComponent(projId)}/urls/${encodeURIComponent(urlId)}/process`);
  }

  /** Explicitly stop the process tree owned for this URL. */
  stopProjectUrlProcess(projId: string, urlId: string) {
    return this.http.delete<ProjectUrlProcessSnapshot>(
      `${this.baseUrl}/projects/${encodeURIComponent(projId)}/urls/${encodeURIComponent(urlId)}/process`);
  }

  /** AGT-2180 — full actionable diagnosis (process, TCP, HTTP, content). */
  diagnoseProjectUrl(projId: string, urlId: string) {
    return this.http.get<ProjectUrlDiagnostic>(
      `${this.baseUrl}/projects/${encodeURIComponent(projId)}/urls/${encodeURIComponent(urlId)}/diagnostic`);
  }

  /** AGT-2180 — bounded quick-setup validation; never persists the candidate. */
  testProjectUrlSetup(projId: string, body: { label?: string; url: string; startRule: ProjectUrlStartRule | null }) {
    return this.http.post<ProjectUrlDiagnostic>(
      `${this.baseUrl}/projects/${encodeURIComponent(projId)}/urls/test`, body);
  }


  /**
   * F46 — destructive project delete. The backend removes the on-disk
   * project storage (every lane + task), drops the matching WatchPaths
   * entry, then removes the registry record — deterministically and
   * without leaving an orphan folder behind. Returns the deleted record's
   * id / displayName / storageLocation so the UI can surface a toast and
   * purge any stale tabs keyed by the old project name.
   */
  deleteRegistryProject(projId: string) {
    return this.http.delete<{ deletedId: string; displayName: string; storageLocation: string }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projId)}`);
  }

  /**
   * Create a new (empty) workspace. The backend slugs the name into a
   * folder under `{TaskRepository}/projects/{slug}`, materialises the
   * directory, and appends a `WatchPathEntry` to `appsettings.Local.json`.
   * Returns the resolved entry; throws via HttpClient on validation
   * (400) or name collision (409).
   */
  createWorkspace(name: string) {
    return this.http.post<WatchPathEntry>(`${this.baseUrl}/watch-paths`, { name });
  }

  /**
   * Remove a workspace by name. The backend refuses (HTTP 409) when the
   * workspace still contains job folders, returning the live `jobCount`
   * in the error body so the UI can render a clear "still has N jobs"
   * message. The on-disk folder is left in place so a re-create with
   * the same name is reversible.
   */
  deleteWorkspace(name: string) {
    return this.http.delete<{ name: string }>(
      `${this.baseUrl}/watch-paths/${encodeURIComponent(name)}`,
    );
  }

  createJob(req: CreateTaskRequest) {
    return this.http.post<{ id: string; routing?: import('../models/task.model').ComponentRoutingResolution | null }>(`${this.baseUrl}/tasks`, req);
  }

  resolveComponentRouting(body: import('../models/task.model').ComponentRoutingRequest) {
    return this.http.post<import('../models/task.model').ComponentRoutingResolution>(
      `${this.baseUrl}/component-routing/resolve`, body);
  }

  updateOwnershipMapping(
    projectId: string,
    mappingId: string,
    body: import('../models/task.model').ComponentOwnershipMapping,
  ) {
    return this.http.put<import('../models/task.model').ComponentOwnershipMapping>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectId)}/ownership-mappings/${encodeURIComponent(mappingId)}`,
      body,
    );
  }

  /**
   * Pre-filled coding-task draft derived from a finished planning task. The
   * detail view feeds this into the create-task modal (see
   * CreateTaskFormService.openPromotePlanning). 400 when the source is not a
   * planning task.
   */
  getPromoteToCoding(jobId: string, watchPath?: string) {
    return this.http.get<PromoteToCodingResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/promote-to-coding`,
      this.withWatchPath(watchPath),
    );
  }

  getPromoteConcept(jobId: string, watchPath?: string) {
    return this.http.get<import('../models/task.model').PromoteConceptResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/promote-concept`,
      this.withWatchPath(watchPath),
    );
  }

  promoteConcept(jobId: string, itemIndexes: number[], watchPath?: string) {
    return this.http.post<import('../models/task.model').PromoteConceptTasksResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/promote-concept`,
      { itemIndexes },
      this.withWatchPath(watchPath),
    );
  }

  /**
   * AGT-2069 — declare (or clear) "no follow-up intended" on a planning task.
   * This satisfies the spawn-contract completion gate without producing
   * follow-up cards, by an explicit operator call. Returns the recomputed
   * spawn summary so the detail can update without a re-fetch. 400 when the
   * task is not a planning task.
   */
  setPlanningClosure(jobId: string, declared: boolean, reason: string | null, watchPath?: string) {
    return this.http.post<import('../models/task.model').PlanningSpawnSummary>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/planning-closure`,
      { declared, reason },
      this.withWatchPath(watchPath),
    );
  }

  setConceptDossier(
    jobId: string,
    body: { path?: string | null; noDossierNeeded: boolean; reason?: string | null },
    watchPath?: string,
  ) {
    return this.http.post<import('../models/task.model').ConceptDossierSummary>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/concept-dossier`,
      body,
      this.withWatchPath(watchPath),
    );
  }

  // Tag registry + per-job tag mutation. Backlog-lane spec.
  listTags() {
    return this.http.get<import('../models/task.model').TagRegistryEntry[]>(`${this.baseUrl}/tags`);
  }

  createTag(req: { id?: string; label: string; color?: string; description?: string }) {
    return this.http.post<import('../models/task.model').TagRegistryEntry>(
      `${this.baseUrl}/tags`,
      req,
    );
  }

  deleteTag(id: string) {
    return this.http.delete(`${this.baseUrl}/tags/${encodeURIComponent(id)}`);
  }

  setJobTags(jobId: string, tags: string[], watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/tags`,
      { tags },
      this.withWatchPath(watchPath),
    );
  }

  /**
   * F34 / AGT-2029: replace-all write of a task's cross-references. Each list
   * becomes the full set for its relation kind. Hard errors (self-reference,
   * dependsOn cycle) return 400 with a per-edge `errors[]` body. An unknown key
   * is NOT a hard failure - the waits-on target may be created later - so the
   * write persists and the unknown edges come back as `warnings[]`. Returns the
   * `{ references, warnings }` envelope on success.
   */
  setTaskReferences(
    jobId: string,
    references: import('../models/task.model').TaskReferences,
    watchPath?: string,
  ) {
    return this.http.put<import('../models/task.model').SetTaskReferencesResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/references`,
      references,
      this.withWatchPath(watchPath),
    );
  }

  /** Explicit content approval for release-gated dependents; never inferred from lane state. */
  setTaskReleased(jobId: string, released: boolean, watchPath?: string) {
    return this.http.put<{ released: boolean }>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/release`,
      { released },
      this.withWatchPath(watchPath),
    );
  }

  /**
   * F34 reverse-index: tasks that reference this one. Optional `kind` narrows
   * to a single relation (e.g. `dependsOn` for the "who depends on X" filter).
   * Empty array when nothing points at the task (or it has no stable key).
   */
  getTaskDependents(
    jobId: string,
    kind?: import('../models/task.model').TaskReferenceKind,
    watchPath?: string,
  ) {
    const base = this.withWatchPath(watchPath);
    const params = kind ? (base.params ?? new HttpParams()).set('kind', kind) : base.params;
    return this.http.get<import('../models/task.model').TaskReferenceLink[]>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/dependents`,
      params ? { params } : {},
    );
  }

  /**
   * AGT-2050 batch projection (the same one the inline microcard hydrator uses):
   * resolve a set of task keys to their compact live-or-ghost reference status.
   * Keys whose project short-code is unknown are dropped by the backend, so the
   * result may be shorter than the input; a known key with no live task comes
   * back as a ghost (`exists === false`). Reused by the wiki cross-reference
   * panel so a wiki page renders its related tasks with the very same microcard.
   */
  getReferenceStatuses(keys: string[]) {
    return this.http
      .post<{ items: import('../components/task-reference-microcard/task-reference-microcard').TaskReferenceStatus[] }>(
        `${this.baseUrl}/tasks/reference-status`,
        { keys },
      )
      .pipe(map((r) => r.items ?? []));
  }

  /**
   * The deployment-configured default CLI + model for the code-review step.
   * The panel seeds its picker from this when the operator has no remembered
   * last-used pair, so a `CodeReviewStep:DefaultModel` set in appsettings
   * actually surfaces in the UI instead of a hard-coded guess.
   */
  codeReviewDefaults() {
    return this.http.get<CodeReviewDefaults>(`${this.baseUrl}/tasks/code-review/defaults`);
  }

  /**
   * List the code-review-step artifacts for one job. Each entry carries the
   * frontmatter fields (verdict, summary, model, runAt) so the panel can
   * render rows without fetching every MD body.
   */
  listCodeReviews(jobId: string, watchPath?: string) {
    return this.http.get<{ entries: CodeReviewListEntry[] }>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/code-review/list`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * AGT-2717: the canonical merge of every review attempt across both planes.
   * Task detail already carries the same object as `info.reviewProjection`;
   * use this when a caller only needs the review head without the full
   * task-detail payload.
   */
  getReviewProjection(jobId: string, watchPath?: string) {
    return this.http.get<ReviewProjectionView>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/review-projection`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Run one user-triggered code-review step against the job's most recent
   * commit. Synchronous: the response arrives once the underlying CLI call
   * has finished, so the UI can keep a spinner up for the duration.
   */
  runCodeReview(
    jobId: string,
    body: { model?: string; cliType?: string; thinkingLevel?: string | null; commit?: string; mode?: 'verdict' | 'grade' },
    watchPath?: string,
  ) {
    return this.http.post<CodeReviewRunResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/code-review`,
      body,
      this.withWatchPath(watchPath),
    );
  }

  /** Add an implemented post-step to an existing card and run only that step. */
  runTaskPostStep(jobId: string, stepId: string, watchPath?: string) {
    return this.http.post<{
      stepId: string;
      attempt: number;
      status: string;
      summary: string;
      artifactRef?: string | null;
    }>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/pipeline/steps/${encodeURIComponent(stepId)}/run`,
      { watchPath, addToCard: true },
    );
  }

  /**
   * Read one code-review MD body. Used by the panel to expand a row inline.
   */
  readCodeReview(jobId: string, fileName: string, watchPath?: string) {
    return this.http.get<{ fileName: string; content: string }>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/code-review/${encodeURIComponent(fileName)}`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Acknowledge or un-acknowledge a review-evidence finding. Append-only:
   * the backend writes a new line into `results/review-evidence.jsonl`
   * with the same `id` and the updated `acknowledged` flag.
   */
  acknowledgeReviewEvidence(
    jobId: string,
    evidenceId: string,
    acknowledged: boolean,
    watchPath?: string,
  ) {
    return this.http.post(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/review-evidence/${encodeURIComponent(evidenceId)}/acknowledge`,
      { acknowledged },
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Create a queued follow-up task in the same project, prefilled with the
   * finding's title + body + linked artifacts/file refs. Returns the new
   * job's id plus its stable key so the UI can route without exposing the
   * project's filesystem location.
   */
  createReviewEvidenceFollowup(
    jobId: string,
    evidenceId: string,
    body: { title?: string; targetState?: string },
    watchPath?: string,
  ) {
    return this.http.post<{ jobId: string; taskKey?: string; targetState: string }>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/review-evidence/${encodeURIComponent(evidenceId)}/follow-up`,
      body,
      this.withWatchPath(watchPath),
    );
  }

  setJobTaskType(jobId: string, taskType: string, watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/task-type`,
      { taskType },
      this.withWatchPath(watchPath),
    );
  }

  updateJobFile(jobId: string, fileName: string, content: string, watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/files/${encodeURIComponent(fileName)}`,
      { content },
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Lists supported Markdown, HTML, and aspect JSON documents in the job root
   * (status.md excluded). Drives the Files tab in the detail view; cheap
   * manifest call so the tab can fetch individual contents lazily through
   * {@link readJobFile}.
   */
  listJobArtifacts(jobId: string, watchPath?: string) {
    return this.http.get<TaskArtifactsResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/artifacts`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Reads one file from the job root. Used by the Files tab to lazily
   * fetch the content of an aspect, note, HTML, or other document card when
   * the user expands it. Returns the body as plain text.
   */
  readJobFile(jobId: string, fileName: string, watchPath?: string) {
    const opts = this.withWatchPath(watchPath);
    return this.getUtf8Text(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/files/${encodeURIComponent(fileName)}`,
      opts,
    );
  }

  /** Git-backed history for one task file, served by the Slice 2 file-source API. */
  getTaskFileHistory(
    jobId: string,
    path: string,
    watchPath?: string,
    scope: TaskFileSourceScope = 'auto',
  ) {
    return this.http.get<TaskFileHistoryEntry[]>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/files/${this.encodeTaskFilePath(path)}/history`,
      this.withFileSourceParams(watchPath, scope),
    );
  }

  /** Previous generated Results, including workspace-git backfill for legacy cards. */
  getResultHistory(jobId: string, watchPath?: string) {
    return this.http.get<ResultHistoryEntry[]>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/result-history`,
      this.withWatchPath(watchPath),
    );
  }

  /** Read one immutable previous Result selected from the Result tab. */
  readResultHistory(jobId: string, versionId: string, watchPath?: string) {
    return this.http.get<ResultHistoryDocument>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/result-history/${encodeURIComponent(versionId)}`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Read the current (live) content of a task file. With `scope: 'code'`
   * this reads an arbitrary source file relative to the repo root (the
   * backend resolves + guards it with `IsWithin`), which is what the
   * clickable protocol source references use to open a file in the
   * source viewer. Returns the body as plain UTF-8 text.
   */
  readTaskFile(
    jobId: string,
    path: string,
    watchPath?: string,
    scope: TaskFileSourceScope = 'auto',
  ) {
    return this.getUtf8Text(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/files/${this.encodeTaskFilePath(path)}`,
      this.withFileSourceParams(watchPath, scope),
    );
  }

  /** Read one file version at a specific commit SHA. */
  readTaskFileAt(
    jobId: string,
    path: string,
    sha: string,
    watchPath?: string,
    scope: TaskFileSourceScope = 'auto',
  ) {
    return this.getUtf8Text(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/files/${this.encodeTaskFilePath(path)}`,
      this.withFileSourceParams(watchPath, scope, { at: sha }),
    );
  }

  reorderJobs(jobs: TaskOrderItem[]) {
    return this.http.post(`${this.baseUrl}/tasks/reorder`, { jobs });
  }

  /**
   * "Do Next" from the detail view: ask the backend to atomically promote
   * this job to the head of its project's ready queue. Single round-trip,
   * no client-side knowledge of sibling jobs required.
   */
  moveJobToTop(jobId: string, watchPath?: string) {
    return this.http.post<{ position: number }>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/move-to-top`,
      null,
      this.withWatchPath(watchPath),
    );
  }

  changeProject(jobId: string, targetWatchPath: string, watchPath?: string) {
    return this.http.post(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/change-project`,
      { targetWatchPath },
      this.withWatchPath(watchPath),
    );
  }

  deleteJob(jobId: string, watchPath?: string) {
    return this.http.delete(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}`,
      this.withWatchPath(watchPath),
    );
  }

  // Git
  getGitStatus(jobId: string, watchPath?: string) {
    return this.http.get<GitStatus>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/git/status`,
      this.withWatchPath(watchPath),
    );
  }

  getGitDiff(jobId: string, path: string | null, watchPath?: string) {
    // `path` scopes the diff to one file. It MUST go through HttpParams.set
    // (via withWatchPathAndPath) — assigning it as a plain property on the
    // immutable HttpParams from withWatchPath() is silently ignored by
    // HttpClient, which dropped the param whenever watchPath was present and
    // made the backend return the whole-tree diff instead of the file's.
    const opts = path ? this.withWatchPathAndPath(watchPath, path) : this.withWatchPath(watchPath);
    return this.getUtf8Text(`${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/git/diff`, opts);
  }

  commitJob(jobId: string, message: string, watchPath?: string) {
    return this.http.post<{ sha?: string }>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/git/commit`,
      { message },
      this.withWatchPath(watchPath),
    );
  }

  generateCommitMessage(jobId: string, watchPath?: string) {
    return this.http.post<{ message: string }>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/git/generate-message`,
      {},
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Commit-provenance & landed-state (ASS-1724). Persisted append-only facts
   * plus the live graph-derived landed-state, ladder, and per-commit membership.
   * Recomputed server-side on every call so it tracks develop/main as they move.
   */
  getTaskProvenance(jobId: string, watchPath?: string) {
    return this.http.get<TaskProvenanceView>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/provenance`,
      this.withWatchPath(watchPath),
    );
  }

  // Per-task commit snapshot — what the auto-commit recorded on the
  // progress→review transition, plus a live re-derivation of the file list.
  getJobCommit(jobId: string, watchPath?: string) {
    return this.http.get<TaskCommitDetail>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/commit`,
      this.withWatchPath(watchPath),
    );
  }

  getJobCommitDiff(jobId: string, path: string | null, watchPath?: string) {
    // See getGitDiff: scope the commit diff to one path via HttpParams.set,
    // not a plain property on the immutable HttpParams (which is dropped).
    const opts = path ? this.withWatchPathAndPath(watchPath, path) : this.withWatchPath(watchPath);
    return this.getUtf8Text(`${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/commit/diff`, opts);
  }

  /**
   * Aggregated file list across every commit attributed to this task. This is
   * the default review view when a task carries a commit chain.
   */
  getJobCommitFilesAggregate(jobId: string, watchPath?: string) {
    return this.http.get<{ files: GitFileChange[] }>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/commits/files`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Aggregated diff across every commit attributed to this task, optionally
   * scoped to one path. The backend concatenates only the task-owned commits.
   */
  getJobCommitDiffAggregate(jobId: string, path: string | null, watchPath?: string) {
    const opts = path ? this.withWatchPathAndPath(watchPath, path) : this.withWatchPath(watchPath);
    return this.http.get<{ diff: string }>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/commits/diff`,
      opts,
    );
  }

  /**
   * File list for a specific commit in this task's commit chain. Validates
   * server-side that the SHA actually belongs to this job, so the endpoint
   * cannot be coaxed into showing arbitrary repository history.
   */
  getJobCommitFilesBySha(jobId: string, sha: string, watchPath?: string) {
    return this.http.get<{ sha: string; files: GitFileChange[] }>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/commits/${encodeURIComponent(sha)}/files`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Diff text for a specific commit in this task's commit chain, optionally
   * scoped to one path. Drives the multi-commit detail view when the user
   * picks any commit other than the latest.
   */
  getJobCommitDiffBySha(jobId: string, sha: string, path: string | null, watchPath?: string) {
    // See getGitDiff: scope to one path via HttpParams.set, not a plain
    // property on the immutable HttpParams (which is dropped).
    const opts = path ? this.withWatchPathAndPath(watchPath, path) : this.withWatchPath(watchPath);
    return this.http.get<{ diff: string }>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/commits/${encodeURIComponent(sha)}/diff`,
      opts,
    );
  }

  /**
   * Full working-tree text of one file, for the git-pane's rendered md/html
   * preview (AGT-2008). Returns `{ content, isBinary }`; a binary blob comes
   * back with empty content + `isBinary: true` so the pane shows a
   * "not previewable" note instead of raw bytes.
   */
  getGitFileContent(jobId: string, path: string, watchPath?: string) {
    return this.http.get<GitFileContentResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/git/file`,
      this.withWatchPathAndPath(watchPath, path),
    );
  }

  /**
   * File text at a specific commit in this task's chain, for the commit-mode
   * md/html preview. Mirrors {@link getGitFileContent}; the SHA is validated
   * server-side as a known job commit.
   */
  getJobCommitFileBySha(jobId: string, sha: string, path: string, watchPath?: string) {
    return this.http.get<GitFileContentResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/commits/${encodeURIComponent(sha)}/file`,
      this.withWatchPathAndPath(watchPath, path),
    );
  }

  openInVsCode(jobId: string, watchPath?: string) {
    return this.http.post(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/open-in-vscode`,
      {},
      this.withWatchPath(watchPath),
    );
  }

  getClaudeSessionInfo(jobId: string, watchPath?: string) {
    return this.http.get<ClaudeSessionResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/claude/session-info`,
      this.withWatchPath(watchPath),
    );
  }

  /** Per-job session-event log: start/continue/recovery rows + sessionChain. */
  getSessionEvents(jobId: string, watchPath?: string) {
    return this.http.get<SessionEventsResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/session-events`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Per-job derived view of "what the agent actually did" - folded from
   * session-events.jsonl + tool-calls.jsonl. Drives the Overview tab's
   * Agent Work block.
   */
  getAgentWorkSummary(jobId: string, watchPath?: string) {
    return this.http.get<AgentWorkSummary>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/agent-work-summary`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Drill-down companion to {@link getAgentWorkSummary}: the same
   * tool-calls.jsonl rows folded into per-tool groups, each carrying the
   * individual calls (command / file / pattern + outcome) so the Overview
   * tab can show *what* the agent did, not just a count. Fetched lazily on
   * first expand.
   */
  getAgentWorkDetail(jobId: string, watchPath?: string) {
    return this.http.get<AgentWorkDetail>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/agent-work-detail`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Per-job task plan that drives the plan strip above the activity log:
   * the agent's own TodoWrite / update_plan items with sub-actions derived
   * by replaying plan-snapshots.jsonl + tool-calls.jsonl. Read-only, no
   * model call.
   */
  getPlan(jobId: string, watchPath?: string) {
    return this.http.get<TaskPlanView>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/plan`,
      this.withWatchPath(watchPath),
    );
  }

  /** Per-job run timeline: ordered list of CLI invocations + aggregates. */
  getRunTimeline(jobId: string, watchPath?: string) {
    return this.http.get<RunTimeline>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/runs`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Per-job pipeline: the static step catalogue this job targets, the
   * recorded per-step execution, a derived per-step + task-total cost
   * breakdown, and the per-project step config. Drives the Overview
   * pipeline block (pre/post steps, status, per-step tokens/cost, total).
   */
  getJobPipeline(jobId: string, watchPath?: string) {
    return this.http.get<TaskPipelineResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/pipeline`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Raw step-call prompts captured at central dispatch into
   * `.metadata/prompts.jsonl` (aspects, code-review-grade, ...). The Overview
   * "Prompt" affordance on a pipeline step reads this to show the exact prompt
   * that step sent to the CLI. Main-run prompts / follow-ups are deliberately
   * absent here — they already live in `prompt.md` / chat.
   */
  getStepPrompts(jobId: string, watchPath?: string) {
    return this.http.get<StepPromptsResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/step-prompts`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Per-task event ledger (`logs/timeline.jsonl`, ADR-0049 / ASS-566):
   * the unified chronological list of lifecycle events including the
   * orchestrator's completion-loop verdicts (accept / reopen / escalate).
   * Drives the Overview attempt-cycle indicator + the Timeline tab.
   */
  getTaskTimeline(jobId: string, watchPath?: string) {
    return this.http.get<TaskTimelineEvent[]>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/timeline`,
      this.withWatchPath(watchPath),
    );
  }

  /** Commits whose author date falls inside the given run's wall-clock window. */
  getRunCommits(jobId: string, runIndex: number, watchPath?: string) {
    return this.http.get<RunCommitsResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/runs/${runIndex}/commits`,
      this.withWatchPath(watchPath),
    );
  }

  /** Aggregated file list for one run's SHA range - drives the run git viewer's file tree. */
  getRunFiles(jobId: string, runIndex: number, watchPath?: string) {
    return this.http.get<RunFilesResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/runs/${runIndex}/files`,
      this.withWatchPath(watchPath),
    );
  }

  /** Unified diff for one path inside a run's SHA range. */
  getRunDiff(jobId: string, runIndex: number, path: string, watchPath?: string) {
    return this.http.get<RunDiffResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/runs/${runIndex}/diff`,
      this.withWatchPathAndPath(watchPath, path),
    );
  }

  /** The exact context (rendered prompt) handed to the agent for one run. Fetched on demand from the run card. */
  getRunContext(jobId: string, runIndex: number, watchPath?: string) {
    return this.http.get<RunContextResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/runs/${runIndex}/context`,
      this.withWatchPath(watchPath),
    );
  }

  getRegressionRadar(jobId: string, watchPath?: string) {
    return this.http.get<RegressionRadarResult>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/regression-radar`,
      this.withWatchPath(watchPath),
    );
  }

  getProjectRegressionRadar(projectName: string) {
    return this.http.get<RegressionRadarResult>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/regression-radar`,
    );
  }

  // CLI execution
  startJob(jobId: string, watchPath?: string, model?: string, cliType?: CliType, thinkingLevel?: string) {
    const body: { model?: string; cliType?: CliType; thinkingLevel?: string } = {};
    if (model) body.model = model;
    if (cliType) body.cliType = cliType;
    if (thinkingLevel) body.thinkingLevel = thinkingLevel;
    return this.http.post<ContinueTaskResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/start`,
      body,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Pause the running CLI for this job. The optional `reason` flows into
   * the backend's RunStatusClassifier:
   *   - 'user'     (default) - explicit Pause button. Resulting status is
   *                  'stopped'; the UI may show a small toast.
   *   - 'followup' - Pause-and-Send: the UI will immediately call
   *                  continueJob afterwards, so applyExecutionState should
   *                  not pop a modal for the in-flight 'stopped' frame.
   * The string is forwarded as ?reason=...; the backend coerces unknown
   * values to 'user'.
   */
  stopJob(jobId: string, watchPath?: string, reason: 'user' | 'followup' = 'user') {
    const base = this.withWatchPath(watchPath);
    const params = (base.params ?? new HttpParams()).set('reason', reason);
    return this.http.post(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/stop`,
      {},
      { ...base, params },
    );
  }

  continueJob(
    jobId: string,
    prompt: string,
    watchPath?: string,
    model?: string,
    cliType?: CliType,
    thinkingLevel?: string,
    mode?: ContinueMode,
  ) {
    const body: { prompt: string; model?: string; cliType?: CliType; thinkingLevel?: string; mode?: ContinueMode } = {
      prompt,
    };
    if (model) body.model = model;
    if (cliType) body.cliType = cliType;
    if (thinkingLevel) body.thinkingLevel = thinkingLevel;
    if (mode) body.mode = mode;
    return this.http.post<ContinueTaskResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/continue`,
      body,
      this.withWatchPath(watchPath),
    );
  }

  setJobModel(jobId: string, model: string | null, watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/model`,
      { model },
      this.withWatchPath(watchPath),
    );
  }

  setJobThinkingLevel(jobId: string, thinkingLevel: string | null, watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/thinking-level`,
      { thinkingLevel },
      this.withWatchPath(watchPath),
    );
  }

  setJobCliType(jobId: string, cliType: CliType, watchPath?: string, useOwnSession?: boolean) {
    const body: { cliType: CliType; useOwnSession?: boolean } = { cliType };
    if (useOwnSession !== undefined) body.useOwnSession = useOwnSession;
    return this.http.put(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/cli-type`,
      body,
      this.withWatchPath(watchPath),
    );
  }

  getCliModelCatalog(cliType: CliType, refresh = false) {
    const params = refresh ? new HttpParams().set('refresh', 'true') : undefined;
    return this.http.get<CliModelCatalog>(
      `${this.baseUrl}/cli/${cliType}/models`,
      params ? { params } : {},
    );
  }

  getCliUsageReport() {
    return this.http.get<CliUsageReport>(`${this.baseUrl}/cli/usage`);
  }

  /**
   * Lazy deep-read of one CLI session (model, thinking, message count, first
   * prompt, git branch). Fetched only when a session row is expanded so the
   * inventory list never reads transcript bodies.
   */
  getCliSessionDetail(cliType: CliType, id: string, cwd: string | null) {
    return this.http.get<CliSessionDetail>(
      `${this.baseUrl}/cli/${encodeURIComponent(cliType)}/session-detail`,
      { params: cwd ? { id, cwd } : { id } },
    );
  }

  /** Guarded cleanup delete of a single session transcript. The backend refuses paths outside the CLI session store. */
  deleteCliSession(cliType: CliType, id: string, cwd: string | null) {
    return this.http.delete<CliSessionDeleteResult>(
      `${this.baseUrl}/cli/${encodeURIComponent(cliType)}/session`,
      { params: cwd ? { id, cwd } : { id } },
    );
  }

  /** Per-CLI completion contracts (how each backend signals turn completion). */
  getCliCompletionContracts() {
    return this.http.get<CliCompletionContract[]>(`${this.baseUrl}/cli/contracts`);
  }

  /** Per-CLI working-memory report: memory / session state plus protected auth / config (ASS-1748 / T1c). */
  getCliWorkingMemory(cliType: CliType) {
    return this.http.get<CliWorkingMemoryReport>(
      `${this.baseUrl}/cli/${encodeURIComponent(cliType)}/working-memory`,
    );
  }

  /** Delete one memory / session state by absolute path. The backend refuses auth / config paths. */
  deleteCliWorkingMemory(cliType: CliType, path: string) {
    return this.http.delete<CliWorkingMemoryDeleteResult>(
      `${this.baseUrl}/cli/${encodeURIComponent(cliType)}/working-memory`,
      { params: { path } },
    );
  }

  // Cycle 10d: quota / subscription rate-limit reporting moved to
  // QuotaApiService (`features/quota/services/`). Caller migration:
  // `inject(QuotaApiService)` instead of `inject(TaskService)` + the
  // method names stay identical.

  setJobTitle(jobId: string, title: string, watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/title`,
      { title },
      this.withWatchPath(watchPath),
    );
  }

  // --- Epics -------------------------------------------------------------
  // An epic is a kind=epic card; its sub-tasks point at it via epicId. The
  // rollup is derived live server-side so progress always matches the board.

  /** Assignment way 2: attach (epicId) or detach (null/'') a task to an epic. */
  setJobEpic(jobId: string, epicId: string | null, watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/epic`,
      { epicId: epicId ?? '' },
      this.withWatchPath(watchPath),
    );
  }

  /** All epics with their live sub-task rollups. */
  getEpics(includeFixtures = false, status?: 'active' | 'completed', project?: string) {
    let params = new HttpParams();
    if (includeFixtures) params = params.set('includeFixtures', 'true');
    if (status) params = params.set('status', status);
    if (project) params = params.set('project', project);
    return this.http.get<import('../models/task.model').EpicRollup[]>(
      `${this.baseUrl}/epics`,
      { params },
    );
  }

  getCompletedEpicCount(includeFixtures = false, project?: string) {
    let params = new HttpParams();
    if (includeFixtures) params = params.set('includeFixtures', 'true');
    if (project) params = params.set('project', project);
    return this.http.get<{ count: number }>(`${this.baseUrl}/epics/completed/count`, { params });
  }

  /** A single epic's rollup. */
  getEpic(epicId: string, watchPath?: string) {
    return this.http.get<import('../models/task.model').EpicRollup>(
      `${this.baseUrl}/epics/${encodeURIComponent(epicId)}`,
      this.withWatchPath(watchPath),
    );
  }

  /** Assignment way 3 (deterministic half): batch-create sub-tasks under an epic. */
  createEpicSubTasks(
    epicId: string,
    req: import('../models/task.model').CreateEpicSubTasksRequest,
    watchPath?: string,
  ) {
    return this.http.post<{ epicId: string; created: string[] }>(
      `${this.baseUrl}/epics/${encodeURIComponent(epicId)}/sub-tasks`,
      req,
      this.withWatchPath(watchPath),
    );
  }

  getModelCatalog() {
    return this.http.get<CliModelCatalog>(`${this.baseUrl}/settings/cli/models`);
  }

  getJobOutput(jobId: string, watchPath?: string) {
    return this.http.get<CliOutputLine[]>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/output`,
      this.withWatchPath(watchPath),
    );
  }

  refreshContextUsage(jobId: string, watchPath?: string) {
    return this.http.post<ContextUsageSnapshot>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/context-usage/refresh`,
      {},
      this.withWatchPath(watchPath),
    );
  }

  regenerateSummary(jobId: string, watchPath?: string) {
    return this.http.post(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/summary/regenerate`,
      {},
      this.withWatchPath(watchPath),
    );
  }

  /**
   * One-shot interim summary while a run is in flight. Calls Haiku
   * against the live cli-output.log and returns the markdown directly;
   * status.md on disk is left untouched so the post-run summary still
   * owns it. Used by the `📊 Interim status` button in the protocol pane.
   */
  requestInterimSummary(jobId: string, watchPath?: string) {
    return this.http.post<{ markdown: string; durationMs: number }>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/summary/interim`,
      {},
      this.withWatchPath(watchPath),
    );
  }

  // Runner management
  getRunnerStatus() {
    return this.http.get<RunnerStatus>(`${this.baseUrl}/runner/status`);
  }

  /**
   * Read the orchestrator's chronological feed for one project: decisions
   * made, actions taken (queued follow-ups, watchdog kills, recovery
   * fallbacks), and eventually user interventions. Backed by
   * `<watchPath>/.orchestrator/orchestrator.jsonl`.
   */
  getOrchestratorLog(projectName: string) {
    return this.http.get<OrchestratorLogResponse>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-log`,
    );
  }

  /** Read the newest orchestrator events across every watched project. */
  getGlobalOrchestratorFeed() {
    return this.http.get<import('../features/orchestrator').GlobalOrchestratorFeedResponse>(
      `${this.baseUrl}/runner/orchestrator-feed`,
    );
  }

  /**
   * Read the per-project list of 4-review jobs whose latest CLI output
   * carries an unresolved [[TASK_NEEDS_INPUT]] sentinel. Drives the
   * project-detail banner: when non-empty, the orchestrator owes a
   * decision (or is about to make one in the next tick).
   */
  getReviewDecisionsPending(projectName: string) {
    return this.http.get<{
      project: string;
      items: { jobId: string; title: string; reason: string | null }[];
    }>(`${this.baseUrl}/projects/${encodeURIComponent(projectName)}/review-decisions-pending`);
  }

  /**
   * ADR-0027: read the *live, in-progress* decision sentinel(s) the named
   * project's running job has emitted. Distinct from
   * getReviewDecisionsPending (post-run, scoped to 4-auto-review): this
   * surface fires while the job is still in 3-progress, the moment the
   * agent prints [[TASK_NEEDS_INPUT]] / [[TASK_BLOCKED]] to stdout.
   */
  getRunnerPendingDecisions(projectName: string) {
    return this.http.get<{
      project: string;
      items: {
        jobId: string;
        title: string;
        kind: string;
        reason: string | null;
        detectedAt: string;
      }[];
    }>(`${this.baseUrl}/runner/${encodeURIComponent(projectName)}/pending-decisions`);
  }

  /**
   * Cycle 5: single-round-trip per-project snapshot. Returns settings,
   * runner status, orchestrator log tail (last 5), orchestrator session,
   * post-run pending review decisions, and live runner pending decisions
   * in one response. project-detail.refreshAll uses this to replace the
   * 6+ parallel polled GETs that pre-Cycle-5 produced ~42 requests every
   * 10 s of idle on this panel alone. Standalone endpoints stay live for
   * other consumers.
   */
  getProjectSnapshot(projectName: string) {
    return this.http.get<ProjectSnapshot>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/snapshot`,
    );
  }

  getPublishPanel(projectName: string, targetId: string) {
    return this.http.get<PublishActionPanel>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/publish/${encodeURIComponent(targetId)}/panel`,
    );
  }

  setPublishAutomation(projectName: string, targetId: string, mode: PublishAutomationMode) {
    return this.http.put<{ targetId: string; mode: PublishAutomationMode }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/publish/automation`,
      { targetId, mode },
    );
  }

  publishPackage(projectName: string, targetId: string, version: string) {
    return this.http.post<PublishWorkflowRun>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/publish/package`,
      { targetId, version },
    );
  }

  deployWebsite(projectName: string) {
    return this.http.post<PublishWorkflowRun>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/publish/website`,
      { targetId: 'website' },
    );
  }

  getPublishRun(projectName: string, targetId: string) {
    return this.http.get<PublishWorkflowRun>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/publish/${encodeURIComponent(targetId)}/run`,
    );
  }

  getPendingCrashRecoveries() {
    return this.http.get<{ pending: CrashRecoveryPending[] }>(
      `${this.baseUrl}/crash-recovery/pending`,
    );
  }

  commitCrashRecovery(id: string) {
    return this.http.post<CrashRecoveryActionResult>(
      `${this.baseUrl}/crash-recovery/pending/${encodeURIComponent(id)}/commit`,
      {},
    );
  }

  dismissCrashRecovery(id: string) {
    return this.http.post<CrashRecoveryActionResult>(
      `${this.baseUrl}/crash-recovery/pending/${encodeURIComponent(id)}/dismiss`,
      {},
    );
  }

  repairProjectQueueHealth(projectName: string) {
    return this.http.post<{
      project: string;
      moved: unknown[];
      failed: unknown[];
      queueHealth: ProjectSnapshot['queueHealth'];
    }>(`${this.baseUrl}/projects/${encodeURIComponent(projectName)}/queue-health/repair`, {});
  }

  /** All per-project settings (auto-commit, auto-push, runner mode, orchestrator model, pipeline-step overrides). */
  getAllProjectSettings() {
    return this.http.get<
      Record<
        string,
        {
          autoCommit: boolean;
          crashRecoveryEnabled: boolean;
          autoPushStrategy: 'never' | 'on-completed' | 'always-immediate';
          runnerMode: string | null;
          pickupMode: 'auto' | 'manual' | 'paused';
          executionLocation: string;
          orchestratorModel: string | null;
          buildProfilePickupAllowed?: boolean;
          buildProfile?: { status?: string | null } | null;
          // F35: resolved per-lane sort strategy map (every lane key present).
          laneSortStrategies?: Record<string, string>;
          pipelineSteps?: Record<string, PipelineStepSetting>;
          pipelineStepOrder?: string[];
          pipelineStepsByType?: Record<string, Record<string, PipelineStepSetting>>;
          pipelineStepOrderByType?: Record<string, string[]>;
          // Per-CLI effective permission mode (YOLO default), one entry per CLI.
          cliModes?: Record<string, { mode: string; source: string; args: string[] }>;
        }
      >
    >(`${this.baseUrl}/projects/settings`);
  }

  /**
   * Read the resolved per-CLI permission modes for one project. `resolved`
   * has one entry per CLI (effective mode + source + spawned args, defaults
   * filled in); `overrides` holds only the CLIs the operator explicitly set;
   * `available` is the user-selectable mode id list (yolo/workspace-write/...).
   */
  getProjectCliModes(projectName: string) {
    return this.http.get<{
      resolved: Record<string, { mode: string; source: string; args: string[] }>;
      overrides: Record<string, string>;
      available: string[];
    }>(`${this.baseUrl}/projects/${encodeURIComponent(projectName)}/cli-modes`);
  }

  /**
   * Write one CLI's permission mode for a project. Pass an empty string to
   * clear the override (the CLI reverts to global/default = YOLO). Takes
   * effect on the next CLI spawn without a backend restart. Returns the
   * resolved mode + source + spawned args after the write.
   */
  setProjectCliMode(projectName: string, cliType: string, mode: string) {
    return this.http.put<{ cli: string; mode: string; source: string; args: string[] }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/cli-mode`,
      { cliType, mode },
    );
  }

  /**
   * T1b / ASS-1742: read the resolved per-CLI context modes for one project.
   * `resolved` has one entry per CLI (effective mode + source + whether the
   * CLI can actually isolate clean state); `overrides` holds only the CLIs the
   * operator explicitly set; `available` is the user-selectable id list
   * (clean/shared).
   */
  getProjectCliContextModes(projectName: string) {
    return this.http.get<{
      resolved: Record<string, { mode: string; source: string; supported: boolean }>;
      overrides: Record<string, string>;
      available: string[];
    }>(`${this.baseUrl}/projects/${encodeURIComponent(projectName)}/cli-context-modes`);
  }

  /**
   * T1b / ASS-1742: write one CLI's context mode for a project. Pass an empty
   * string to clear the override (the CLI reverts to the platform default =
   * CLEAN). Takes effect on the next CLI spawn without a backend restart.
   */
  setProjectCliContextMode(projectName: string, cliType: string, mode: string) {
    return this.http.put<{ cli: string; mode: string; source: string; supported: boolean }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/cli-context-mode`,
      { cliType, mode },
    );
  }

  /**
   * F35: read the resolved per-lane sort strategies for one project. The
   * `resolved` map has every lane key (defaults filled in); `overrides`
   * holds only the lanes the operator has explicitly set; `available` is
   * the user-selectable strategy id list.
   */
  getLaneSortStrategies(projectName: string) {
    return this.http.get<{
      resolved: Record<string, string>;
      overrides: Record<string, string>;
      available: string[];
    }>(`${this.baseUrl}/projects/${encodeURIComponent(projectName)}/lane-sort-strategies`);
  }

  /**
   * F35: write one lane's sort strategy. Pass an empty string to clear the
   * override (the lane reverts to its built-in default). Returns the
   * resolved strategy after the write.
   */
  setLaneSortStrategy(projectName: string, lane: string, strategy: string) {
    return this.http.put<{ lane: string; strategy: string; override: string | null }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/lane-sort-strategy`,
      { lane, strategy },
    );
  }

  /**
   * Configurable pipeline-step catalogue (code-defined steps + capability
   * flags). The Settings panel renders one control row per step from this,
   * so the step list is never hardcoded on the frontend.
   */
  getPipelineCatalogue(projectName?: string | null, pipelineType?: string | null) {
    let params = new HttpParams();
    if (projectName) params = params.set('projectName', projectName);
    if (pipelineType) params = params.set('pipelineType', pipelineType);
    return this.http.get<PipelineCatalogue>(
      `${this.baseUrl}/projects/pipeline-catalogue`,
      { params },
    );
  }

  /** Visibility-only pipeline health signals; this endpoint never mutates a task. */
  getProjectPipelineHealth(projectName: string) {
    return this.http.get<PipelineHealthSnapshot>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/pipeline-health`,
    );
  }

  probePipelineStep(projectName: string, stepId: string) {
    return this.http.post<PipelineStepProbeResult>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/pipeline-steps/${encodeURIComponent(stepId)}/probe`,
      {},
    );
  }

  /**
   * Write one per-project pipeline-step override (enabled / mode / model).
   * Omitted fields clear that facet; the backend rejects unknown step ids
   * and unsupported modes. Returns the full updated `pipelineSteps` map.
   */
  setProjectPipelineStep(
    projectName: string,
    step: {
      pipelineType?: string;
      stepId: string;
      enabled?: boolean | null;
      economyModel?: boolean | null;
      maxIterations?: number | null;
      mode?: string | null;
      cliType?: string | null;
      model?: string | null;
      thinkingLevel?: string | null;
      prompt?: string | null;
      condition?: PipelineStepCondition | null;
    },
  ) {
    return this.http.put<{
      stepId: string;
      pipelineType: string;
      pipelineSteps: Record<string, PipelineStepSetting>;
      pipelineStepsByType: Record<string, Record<string, PipelineStepSetting>>;
    }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/pipeline-step`,
      step,
    );
  }

  /** Persist the project-specific order for configurable pipeline steps. */
  setProjectPipelineStepOrder(projectName: string, pipelineType: string, stepIds: readonly string[]) {
    return this.http.put<{
      pipelineType: string;
      pipelineStepOrder: string[];
      pipelineStepOrderByType: Record<string, string[]>;
    }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/pipeline-step-order`,
      { pipelineType, stepIds },
    );
  }

  setProjectAutoCommit(projectName: string, enabled: boolean) {
    return this.http.put(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/auto-commit`,
      { enabled },
    );
  }

  setProjectCrashRecovery(projectName: string, enabled: boolean) {
    return this.http.put(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/crash-recovery`,
      { enabled },
    );
  }

  setProjectAutoPushStrategy(
    projectName: string,
    strategy: 'never' | 'on-completed' | 'always-immediate',
  ) {
    return this.http.put(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/auto-push-strategy`,
      { strategy },
    );
  }

  setProjectOrchestratorModel(projectName: string, model: string | null) {
    return this.http.put(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/orchestrator-model`,
      { model },
    );
  }

  /** ADR-0026: read the per-project orchestrator-prep autonomy level (0..4). */
  getProjectAutonomyLevel(projectName: string) {
    return this.http.get<{ level: number }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/autonomy`,
    );
  }

  /** ADR-0026: write the per-project orchestrator-prep autonomy level. Server clamps to 0..4. */
  setProjectAutonomyLevel(projectName: string, level: number) {
    return this.http.put<{ level: number }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/autonomy`,
      { level },
    );
  }

  /**
   * Read the long-lived orchestrator session for a project. Returns
   * `{ project, session: null }` when no session has been booted yet
   * (e.g. boot is still in flight after app start, or boot failed).
   */
  getOrchestratorSession(projectName: string) {
    return this.http.get<OrchestratorSessionResponse>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-session`,
    );
  }

  /**
   * Read the singleton global orchestrator session. The wire shape mirrors
   * the per-project endpoint (`{ project, session }`) so the UI can reuse
   * the same renderer; `project` comes back as the literal string
   * "(global)" so the user sees that this is the cross-project session.
   */
  getGlobalOrchestratorSession() {
    return this.http.get<OrchestratorSessionResponse>(
      `${this.baseUrl}/runner/global/orchestrator-session`,
    );
  }

  getOrchestratorContextSessions() {
    return this.http.get<import('../features/orchestrator').OrchestratorContextSessionsResponse>(
      `${this.baseUrl}/orchestrator/sessions`,
    );
  }

  /** Read the compact ORCH-1 application digest for one multichat context. */
  getOrchestratorContextDigest(contextKey: string) {
    return this.http.get<OrchestratorContextDigest>(
      `${this.baseUrl}/orchestrator/context/${orchestratorContextChatSegment(contextKey)}`,
    );
  }

  /**
   * Rebuild one context digest on demand. Unlike the cheap read path this
   * explicitly asks the backend to re-probe quota before assembling it.
   */
  refreshOrchestratorContextDigest(contextKey: string) {
    return this.http.post<OrchestratorContextDigest>(
      `${this.baseUrl}/orchestrator/context/${orchestratorContextChatSegment(contextKey)}/refresh`,
      null,
    );
  }

  // Cycle 10d: token-aggregate endpoints moved to TokensApiService
  // (`features/tokens/services/`). Caller migration:
  // `inject(TokensApiService)` instead of `inject(TaskService)` + the
  // method names stay identical. Methods covered: getTokenSummary,
  // getTokenSummaryAggregate, getTokenSummaryAggregateCached,
  // getAdHocUsage, getWorkspaceTokensTimeline.

  /**
   * Project Token Usage panel (slice 8 of the quality-system mockup).
   * Lifetime + last-24h totals plus the Job / Supporting / Orchestrator
   * category split.
   */
  getProjectTokenUsageSummary(projectName: string) {
    return this.http.get<ProjectTokenUsageSummary>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/token-usage/summary`,
    );
  }

  /** Operator Overview throughput, archive-inclusive through lane history. */
  getProjectThroughput(projectName: string) {
    return this.http.get<ProjectThroughputSummary>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/throughput`,
    );
  }

  getProjectVisualEvidence(projectName: string, refresh = false) {
    return this.http.get<ProjectVisualEvidenceQueue>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/visual-evidence`,
      { params: refresh ? { refresh: 'true' } : undefined },
    );
  }

  acknowledgeProjectVisualEvidence(projectName: string, itemId: string) {
    return this.http.post<ProjectVisualEvidenceItem>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/visual-evidence/${encodeURIComponent(itemId)}/acknowledge`,
      {},
    );
  }

  /** Shared DEP-1 read model: latest stable deploy plus current pending delta. */
  getProjectDeploymentSummary(projectName: string) {
    return this.http.get<ProjectDeploymentSummary>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/deployment/summary`,
    );
  }

  /** Project-wide planned/running/completed test-run pipeline with derived card attachments. */
  getProjectTestRuns(projectName: string) {
    return this.http.get<ProjectTestRunsResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/test-runs`,
    );
  }

  compileProjectDeployment(projectName: string, prompt: string) {
    return this.http.post<CompiledDeploymentPrompt>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/deployment/compile`,
      { prompt },
    );
  }

  /**
   * Per-job × per-day heatmap. `days` accepts up to 90; the backend
   * silently snaps out-of-range values to {1..90}.
   */
  getProjectTokenUsageHeatmap(projectName: string, days = 30) {
    const params = new HttpParams().set('days', String(days));
    return this.http.get<ProjectTokenHeatmap>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/token-usage/heatmap`,
      { params },
    );
  }

  /**
   * Top N jobs by total tokens for the panel's "expensive jobs" list.
   * `limit` defaults to 10, capped at 50.
   */
  getProjectExpensiveJobs(projectName: string, limit = 10) {
    const params = new HttpParams().set('limit', String(limit));
    return this.http.get<ProjectExpensiveJobsResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/token-usage/expensive`,
      { params },
    );
  }

  /**
   * Per-run breakdown for one job: every orchestrator call attributed to
   * the job, with deltas vs. the previous call. Drives the drill-down.
   */
  getProjectJobTokenDetail(projectName: string, jobId: string) {
    return this.http.get<ProjectJobTokenDetail>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/token-usage/job/${encodeURIComponent(jobId)}`,
    );
  }

  /**
   * Per-step-kind pipeline cost over time: the "how it develops" trend.
   * Folds every task's pipeline-execution.json into a per-day series per
   * step kind. `days` defaults to 30, capped at 180 by the backend.
   */
  getProjectPipelineCost(projectName: string, days = 30) {
    const params = new HttpParams().set('days', String(days));
    return this.http.get<ProjectPipelineCostTimeline>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/token-usage/pipeline-cost`,
      { params },
    );
  }

  /**
   * Per-job screenshot listing. Walks `<job>/results/` (recursive),
   * captioned by spec/folder, with pass/fail status from the
   * Playwright harvest index when available. Drives the protocol
   * pane's screenshot strip.
   */
  getJobScreenshots(jobId: string, watchPath?: string | null) {
    let params = new HttpParams();
    if (watchPath) params = params.set('watchPath', watchPath);
    return this.http.get<TaskScreenshotsResponse>(
      `${this.baseUrl}/tasks/${encodeURIComponent(jobId)}/screenshots`,
      { params },
    );
  }

  /**
   * Workspace-wide visual evidence reel: every `<job>/results/`
   * screenshot whose mtime falls inside the requested window,
   * newest-first. Optionally narrowed to a single project name.
   */
  getWorkspaceScreenshots(windowHours: number, projectFilter?: string | null) {
    let params = new HttpParams().set('windowHours', String(windowHours));
    if (projectFilter) params = params.set('projectFilter', projectFilter);
    return this.http.get<WorkspaceScreenshotsResponse>(`${this.baseUrl}/workspace/screenshots`, {
      params,
    });
  }

  /**
   * Workspace-level executive summary for the requested window
   * (`GET /api/workspace/summary?windowHours=N`): per-project activity,
   * severity-ranked top decisions, crash evidence, and open human
   * decisions. Read-only; every row references a record on disk.
   */
  getWorkspaceSummary(windowHours: number) {
    const params = new HttpParams().set('windowHours', String(windowHours));
    return this.http.get<ExecutiveSummaryResponse>(`${this.baseUrl}/workspace/summary`, { params });
  }

  /**
   * Read the Task Server-owned per-project Orchestrator Chat transcript.
   * The legacy project JSONL is a migration source, not active authority.
   */
  getOrchestratorChat(projectName: string) {
    return this.http.get<OrchestratorChatResponse>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-chat`,
    );
  }

  /**
   * MC-2 (Concept §4): read the transcript for a specific navigation context.
   * The side sheet derives a `project:<PROJ>` or `task:<PROJ>/<KEY>` context
   * key from where the operator is (board vs. task page); this hits
   * `GET /api/runner/{contextKey}/orchestrator-chat` so a pinned task and the
   * board no longer share one history. Reading a task context materializes it
   * in the central managed-context list without changing Task Activity. A
   * `project:` context resolves to the canonical project transcript.
   */
  getOrchestratorChatByContext(contextKey: string) {
    return this.http.get<OrchestratorChatResponse>(
      `${this.baseUrl}/runner/${orchestratorContextChatSegment(contextKey)}/orchestrator-chat`,
    );
  }

  /**
   * Send a user message to the project's orchestrator chat. The backend
   * persists both turns and the context receipt on the Task Server, then
   * returns the orchestrator's reply turn.
   */
  sendOrchestratorChat(
    projectName: string,
    body: {
      text: string;
      attachments?: {
        alt: string;
        relativePath: string;
        inlineBase64?: string | null;
        mimeType?: string | null;
      }[];
      navigationContext?: import('../features/orchestrator').ChatNavigationContext | null;
      contextEnvelope?: import('../features/orchestrator').OrchestratorContextEnvelope | null;
      model?: string | null;
      thinkingLevel?: string | null;
      selectionSource?: 'explicit' | 'inherited';
    },
  ) {
    return this.http.post<{
      project: string;
      reply: OrchestratorChatTurn;
      executionContext?: import('../features/orchestrator').ChatExecutionContext | null;
    }>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-chat`,
      body,
    );
  }

  /**
   * MC-2 (Concept §4): send a user message scoped to a navigation context.
   * Hits `POST /api/runner/{contextKey}/orchestrator-chat` so a task context's
   * turns land in and are read back from its own thread, while a `project:`
   * context resolves to the same canonical per-project thread
   * {@link sendOrchestratorChat} writes to. Prompt execution and usage
   * accounting remain project-scoped while persistence stays context-scoped.
   */
  sendOrchestratorChatByContext(
    contextKey: string,
    body: {
      text: string;
      attachments?: {
        alt: string;
        relativePath: string;
        inlineBase64?: string | null;
        mimeType?: string | null;
      }[];
      navigationContext?: import('../features/orchestrator').ChatNavigationContext | null;
      contextEnvelope?: import('../features/orchestrator').OrchestratorContextEnvelope | null;
      model?: string | null;
      thinkingLevel?: string | null;
      selectionSource?: 'explicit' | 'inherited';
    },
  ) {
    return this.http.post<{
      project: string;
      reply: OrchestratorChatTurn;
      executionContext?: import('../features/orchestrator').ChatExecutionContext | null;
    }>(
      `${this.baseUrl}/runner/${orchestratorContextChatSegment(contextKey)}/orchestrator-chat`,
      body,
    );
  }

  /**
   * One-shot Haiku call that turns a free-text prompt into a short
   * imperative English title. Drives the "Generate" button on the
   * Create-task dialog. Returns immediately when the prompt is empty.
   */
  generateTaskTitle(prompt: string) {
    return this.http.post<{ title: string }>(`${this.baseUrl}/title/generate`, { prompt });
  }

  /**
   * One-shot Haiku call that returns three artefacts derived from the
   * user's free-text prompt: a refined prompt, a one-line intent, and
   * up to five topical tags. Drives the "Enhance" button on the
   * Create-task dialog. Pure preview - no side effects.
   */
  enhancePrompt(prompt: string) {
    return this.http.post<{ refinedPrompt: string; intent: string; tags: string[] }>(
      `${this.baseUrl}/prompt/enhance`,
      { prompt },
    );
  }

  /**
   * Override an orchestrator decision. Appends an intervention entry to
   * the feed, and (when `jobId` is provided) routes `newDirection`
   * through the Continue path as a Steer-mode follow-up.
   */
  overrideOrchestratorEntry(
    projectName: string,
    body: { originalTs: string; jobId: string; newDirection: string },
  ) {
    return this.http.post<{ applied: boolean; error?: string; note?: string }>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-log/override`,
      body,
    );
  }

  setRunnerMode(projectName: string, mode: string) {
    return this.http.put(`${this.baseUrl}/runner/${encodeURIComponent(projectName)}/mode`, {
      mode,
    });
  }

  startRunner(projectName: string) {
    return this.http.post(`${this.baseUrl}/runner/${encodeURIComponent(projectName)}/start`, {});
  }

  stopRunner(projectName: string) {
    return this.http.post(`${this.baseUrl}/runner/${encodeURIComponent(projectName)}/stop`, {});
  }

  refreshRunnerStatus(silent = false): void {
    if (this.runnerRefreshInFlight) {
      this.runnerRefreshQueued = true;
      this.runnerRefreshQueuedSilent &&= silent;
      return;
    }

    this.runnerRefreshInFlight = true;
    this.getRunnerStatus().pipe(
      finalize(() => this.finishRunnerRefresh()),
    ).subscribe({
      next: (status) => this.runnerStatus.set(status),
      error: (err) => {
        if (!silent) {
          this.errorDialog.show(err, {
            title: 'Failed to load runner status',
            fallbackMessage: 'Failed to load runner status',
            source: 'Runner status',
          });
        }
      },
    });
  }

  private finishRunnerRefresh(): void {
    this.runnerRefreshInFlight = false;
    if (!this.runnerRefreshQueued) return;

    const silent = this.runnerRefreshQueuedSilent;
    this.runnerRefreshQueued = false;
    this.runnerRefreshQueuedSilent = true;
    this.refreshRunnerStatus(silent);
  }

  startLiveUpdates(intervalMs = 2000): void {
    if (this.liveUpdateTimer) {
      return;
    }

    // F35: prime the lane-strategy store immediately so the board shows the
    // right indicator on first paint, then refresh it on a slow cadence.
    this.refreshLaneSortStrategies();

    // Primary path: react to mutation pushes the instant they arrive.
    this.startPushUpdates();

    // Fallback heartbeat: reconciles any drift and keeps the board live if the
    // socket is down. Callers can request a faster cadence, but never slower
    // than the heartbeat — the point of push is that 2 s polling is no longer
    // needed.
    const heartbeatMs = Math.max(intervalMs, TaskService.HEARTBEAT_MS);
    this.liveUpdateTimer = setInterval(() => {
      if (typeof document !== 'undefined' && document.hidden) {
        return;
      }

      this.refresh(true);

      // Sort strategy changes rarely; refresh it every other heartbeat.
      this.laneSortStrategyTick = (this.laneSortStrategyTick + 1) % 2;
      if (this.laneSortStrategyTick === 0) {
        this.refreshLaneSortStrategies();
      }
    }, heartbeatMs);
  }

  stopLiveUpdates(): void {
    if (this.liveUpdateTimer) {
      clearInterval(this.liveUpdateTimer);
      this.liveUpdateTimer = null;
    }
    if (this.pushRefreshHandle) {
      clearTimeout(this.pushRefreshHandle);
      this.pushRefreshHandle = null;
    }
    this.jobsHub.stop();
  }

  /**
   * Wire the `/hubs/jobs` push events into the board signals.
   *
   * Two delivery shapes:
   *  - Unambiguous, self-contained payloads (create / update carry the full
   *    {@link TaskInfo}; delete carries id+watchPath) are applied to the local
   *    signals directly — zero round-trip, sub-100 ms cross-tab updates.
   *  - Events without enough payload to patch a single row (move carries no
   *    watchPath; reorder/bulk are inherently "re-pull" signals) trigger one
   *    debounced silent re-fetch, which still lands well under 1 s.
   *
   * Optimistic safety: the local-delta helpers are idempotent and keyed by
   * `watchPath::id`, so a push that echoes the caller's own mutation is a
   * no-op rather than a double-apply. The silent re-fetch path runs through
   * {@link refresh}, which already rejects responses during the optimistic
   * grace window, so an in-flight drag is never clobbered by its own echo.
   *
   * `runnerStatusChanged` and CLI start/finish are bridged too: with the board
   * poll slowed to a heartbeat, these keep the per-card execution badge and
   * the project running-cue as responsive as they were under 2 s polling.
   */
  private startPushUpdates(): void {
    this.jobsHub.start({
      jobCreated: (info) => this.upsertJobLocal(info),
      jobUpdated: (info) => this.upsertJobLocal(info),
      jobDeleted: (e) => this.removeJobLocal(e.id, e.watchPath),
      jobMoved: () => this.scheduleSilentRefresh(),
      jobsReordered: () => this.scheduleSilentRefresh(),
      jobsBulkChanged: () => this.scheduleSilentRefresh(),
      // Quiet, immediate stamp update from the push payload itself (no round
      // trip needed to know the index moved forward), plus a silent re-pull
      // so the merge/integration/publish/test-run signals that repository's
      // cards carry catch up to the new snapshot.
      gitStateChanged: (e) => {
        this.gitStateAt.update((current) =>
          !current || new Date(e.gitStateAt) > new Date(current) ? e.gitStateAt : current);
        this.scheduleSilentRefresh();
      },
      runnerStatusChanged: () => this.refreshRunnerStatus(true),
      cliStarted: () => this.scheduleSilentRefresh(),
      cliFinished: () => this.scheduleSilentRefresh(),
      // Initial connect + every reconnect: re-pull the full board so anything
      // emitted while the socket was down is reconciled.
      reconnected: () => this.refresh(true),
    });
  }

  /** Coalesce a burst of push events into a single silent board re-fetch. */
  private scheduleSilentRefresh(): void {
    if (this.pushRefreshHandle) return;
    this.pushRefreshHandle = setTimeout(() => {
      this.pushRefreshHandle = null;
      this.refresh(true);
    }, TaskService.PUSH_DEBOUNCE_MS);
  }

  /**
   * Insert-or-update a single task in the local `jobs` + `grouped` signals,
   * keyed by `watchPath::id`. Removes the row from whatever lane currently
   * holds it and re-inserts it into the lane for its `state`, ordered by the
   * card's `order` field so the position matches what the grouped endpoint
   * would return. Idempotent.
   */
  private upsertJobLocal(info: TaskInfo): void {
    if (!info || !info.id) return;
    const key = `${info.watchPath}::${info.id}`;

    const flat = this.jobs();
    const idx = flat.findIndex((j) => `${j.watchPath}::${j.id}` === key);
    if (idx >= 0) {
      const next = flat.slice();
      next[idx] = info;
      this.jobs.set(next);
    } else {
      this.jobs.set([...flat, info]);
    }

    const lane = STATE_TO_LANE[info.state];
    const current = this.grouped();
    const next: GroupedJobs = { ...current };
    for (const k of Object.keys(next) as LaneKey[]) {
      const list = next[k] ?? [];
      const filtered = list.filter((j) => `${j.watchPath}::${j.id}` !== key);
      if (filtered.length !== list.length) next[k] = filtered;
    }
    if (lane) {
      next[lane] = [...(next[lane] ?? []), info].sort(
        (a, b) => (a.order ?? 0) - (b.order ?? 0),
      );
    }
    this.grouped.set(next);
  }

  /** Remove a task from the local `jobs` + `grouped` signals. Idempotent. */
  private removeJobLocal(jobId: string, watchPath: string): void {
    const key = `${watchPath}::${jobId}`;

    const flat = this.jobs();
    const nextFlat = flat.filter((j) => `${j.watchPath}::${j.id}` !== key);
    if (nextFlat.length !== flat.length) this.jobs.set(nextFlat);

    const current = this.grouped();
    const next: GroupedJobs = { ...current };
    let changed = false;
    for (const k of Object.keys(next) as LaneKey[]) {
      const list = next[k] ?? [];
      const filtered = list.filter((j) => `${j.watchPath}::${j.id}` !== key);
      if (filtered.length !== list.length) {
        next[k] = filtered;
        changed = true;
      }
    }
    if (changed) this.grouped.set(next);
  }

  // CLI settings
  getCliSettings() {
    return this.http.get<CliSettings>(`${this.baseUrl}/settings/cli`);
  }

  setCliPath(path: string) {
    return this.http.put<CliSettings>(`${this.baseUrl}/settings/cli`, { path });
  }

  testCliPath(path: string) {
    return this.http.post<CliSettings>(`${this.baseUrl}/settings/cli/test`, { path });
  }

  setGitHubToken(token: string) {
    return this.http.put<CliSettings>(`${this.baseUrl}/settings/cli/token`, { token });
  }
}
