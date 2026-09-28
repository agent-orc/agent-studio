import { DestroyRef, Injectable, computed, effect, inject, signal } from '@angular/core';
import { Observable, Subscription, timeout } from 'rxjs';
import { TaskDetail, TaskInfo, TaskState } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';
import { NotificationService } from '../../../services/notification.service';
import { TaskDetailPrefetchService } from './task-detail-prefetch.service';
import { LanePagerService, type LanePagerEntry, type LanePagerSnapshot } from './lane-pager.service';
import { BoardFiltersService } from '../../board/state/board-filters.service';
import { laneLabelFor } from './triage-actions.model';
import { perfMark, perfMeasure } from '../../../utils/perf-tracker';
import {
  clearTaskUrl,
  taskReferenceFromUrl,
  taskUrlKey,
  writeTaskUrl,
  type TaskUrlHistoryMode,
} from './task-url';
import { ProjectLookupService } from '../../../services/project-lookup.service';
import type { TaskCore, TaskDocumentData, TaskResource, ResourceName,
  TaskUsageData, TaskReviewData, TaskHistoryData, TaskGitData, ResourcePhase } from './task-core.model';
import { emptyDetail } from './task-core.model';

export interface TaskDetailLoadError {
  taskLabel: string;
  message: string;
}

const TASK_PAGER_HISTORY_STATE = 'studioTaskPager';

interface TaskBrowserHistoryState {
  [TASK_PAGER_HISTORY_STATE]?: LanePagerSnapshot | null;
}

/**
 * Cycle 9j job-detail-feature service: owns the "currently selected
 * job" state across the shell. Lifted out of `app.ts` per ADR-0034.
 *
 * Responsibilities:
 *   - `selected`        which TaskDetail (if any) the side panel renders
 *   - `triageToast`     transient banner shown by the triage panel
 *   - `triageLanePeers` siblings in the same lane (drives j/k navigation)
 *   - URL sync          `#/tasks/<AGT-NNN>` reproduces the open detail without
 *                       leaking a filesystem path
 *   - request token     drops late getDetail replies so panel doesn't
 *                       flash back open after Esc/lane-cleared close
 *
 * The triage HANDLERS (onTriageMove/Delete/Start, advanceToNextInLane)
 * stay in the shell because they orchestrate TaskService mutations,
 * ErrorDialogService, and the TaskDetailComponent ViewChild
 * (`clearTriageActing`). This service is just the selection state +
 * navigation primitives those handlers call.
 */
@Injectable({ providedIn: 'root' })
export class TaskSelectionService {
  private readonly jobService = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  private readonly pager = inject(LanePagerService);
  private readonly prefetch = inject(TaskDetailPrefetchService);
  private readonly boardFilters = inject(BoardFiltersService);
  private readonly projectLookup = inject(ProjectLookupService);
  private readonly destroyRef = inject(DestroyRef);

  /** How many slots ahead of the current pager index to warm. */
  private static readonly PREFETCH_LOOKAHEAD = 2;
  private static readonly DETAIL_TIMEOUT_MS = 15_000;

  constructor() {
    if (typeof window !== 'undefined') {
      const onPopState = () => this.restoreFromUrl(true);
      window.addEventListener('popstate', onPopState);
      this.destroyRef.onDestroy(() => window.removeEventListener('popstate', onPopState));
    }

    // Ensure the lane-pager snapshot covers the currently selected job.
    // `openDetail` captures synchronously on the board-click path, so this
    // effect is a no-op there. The deep-link / URL-restore path sets
    // `selected` without capturing (no preceding click, and grouped() may
    // still be loading) - the effect re-runs once grouped() lands and
    // captures from the live lane peers so the pager appears without
    // requiring the user to press an arrow key first.
    //
    // `lastEnsuredJobKey` guards against re-capture after a mutation that
    // removes the open job from the snapshot (delete from menu, lane
    // dropdown move): `removeAndAdvance` shrinks the iteration BEFORE
    // `selected` is reset to the next job, so for one effect tick `snap`
    // no longer contains `selected.taskKey`. Without the guard the effect
    // would re-capture from the live (still-stale) grouped lane and
    // clobber the carefully-preserved iteration ordering.
    effect(() => {
      const sel = this.selected();
      if (!sel) {
        this.lastEnsuredJobKey = null;
        return;
      }
      const taskKey = sel.info.taskKey;
      const snap = this.pager.snapshot();
      if (snap && snap.jobs.some(j => j.taskKey === taskKey)) {
        // Existing iteration covers the open job; just keep the index
        // aligned (no-op when already aligned).
        this.pager.reanchorTo(taskKey);
        this.lastEnsuredJobKey = taskKey;
        return;
      }
      if (this.lastEnsuredJobKey === taskKey) return;
      const peers = this.peersForLane(sel.info.state);
      if (peers.length === 0) return;
      this.pager.capture(sel.info.state, peers, taskKey);
      this.lastEnsuredJobKey = taskKey;
    });

    // Detail prefetch: warm the next 1..PREFETCH_LOOKAHEAD entries in the
    // current pager snapshot whenever it changes. This is what makes the
    // accept -> next-task navigation feel instant: by the time the user
    // clicks Mark-as-Done, the next peer's TaskDetail is already cached.
    effect(() => {
      const snap = this.pager.snapshot();
      if (!snap) return;
      const lookahead = TaskSelectionService.PREFETCH_LOOKAHEAD;
      const keep = new Set<string>();
      for (let offset = 1; offset <= lookahead; offset++) {
        const entry = snap.jobs[snap.index + offset];
        if (!entry) break;
        const project = this.projectHandleForStorageReference(entry.watchPath);
        if (!project) continue;
        keep.add(`${project}::${entry.id}`);
        this.prefetch.prefetchCore(entry.id, project);
      }
      this.prefetch.keepLookahead(keep);
    });

    effect(() => {
      const projects = this.projectLookup.allProjects();
      const pending = this.pendingCoreReference;
      if (!pending || projects.length === 0) return;
      queueMicrotask(() => {
        if (this.pendingCoreReference !== pending) return;
        this.pendingCoreReference = null;
        if (taskReferenceFromUrl(new URL(window.location.href)) === pending)
          this.restoreFromUrl();
      });
    });
  }

  private lastEnsuredJobKey: string | null = null;
  private pendingTaskTabReplacement: string | null = null;
  private browserHistoryTaskKey: string | null = null;
  private pendingCoreReference: string | null = null;

  /**
   * Set to `true` when the user starts a triage decision (accept etc.)
   * and consumed by the next selection update so we mark the
   * `accept-to-next-rendered` performance interval exactly once per
   * click. The mark/measure is best-effort: any environment without
   * `performance.mark` (older browsers, SSR) silently no-ops.
   */
  private awaitingNextTaskRender = false;

  /**
   * Stamp the start of the accept -> next-task render measurement.
   * Called by the triage controller right before it tears down the
   * outgoing job so the marker brackets exactly the latency the user
   * feels. Names are stable strings the Playwright budget spec asserts
   * on.
   */
  markAcceptClick(): void {
    this.awaitingNextTaskRender = true;
    try {
      performance.mark('accept-click');
    } catch { /* mark API missing or out of buffer space */ }
  }

  /** Internal: pair the `accept-click` mark with `next-task-rendered`. */
  private markNextTaskRendered(): void {
    if (!this.awaitingNextTaskRender) return;
    this.awaitingNextTaskRender = false;
    try {
      performance.mark('next-task-rendered');
      performance.measure('accept-to-next-task', 'accept-click', 'next-task-rendered');
    } catch { /* the click mark may have been GC'd; not fatal */ }
  }

  readonly selected = signal<TaskDetail | null>(null);
  /** Cheap board snapshot used to paint the task route before detail I/O. */
  readonly detailPreview = signal<TaskInfo | null>(null);
  readonly selectedCore = signal<TaskCore | null>(null);
  readonly resourceStates = signal<Record<ResourceName, { phase: ResourcePhase; reason: string | null }>>({
    git: { phase: 'idle', reason: null }, usage: { phase: 'idle', reason: null },
    review: { phase: 'idle', reason: null }, documents: { phase: 'idle', reason: null },
    history: { phase: 'idle', reason: null },
  });
  private activeRequests: Subscription[] = [];
  private activeProject: string | null = null;
  private activeAttempt: string | null = null;
  private activeCoreVersion: number | null = null;

  /** Monotonic event consumed by the studio shell when Back returns to a non-task URL. */
  readonly browserRouteCleared = signal(0);

  /**
   * True while a navigation fetch (pager step, board click, post-mutation
   * advance) is in flight WITHOUT a prefetched detail to paint instantly.
   * The detail header surfaces it as a small loading indicator so stepping
   * to a not-yet-warmed task gives feedback that the reload is happening.
   * Stays false on the cache-hit fast path — there is nothing to wait for.
   * Always toggled under the `openDetailToken` guard so a stale reply for a
   * superseded navigation never clears the spinner of the current one.
   */
  readonly detailLoading = signal(false);
  readonly detailLoadError = signal<TaskDetailLoadError | null>(null);
  private detailLoadRetry: (() => void) | null = null;

  /**
   * Transient banner shown by the triage panel auto-advance flow.
   * Kept as a signal so the existing template binding in the detail
   * pane (`@if (triageToast(); as toast) { … }`) keeps working; the
   * shared notification stack mirrors the same message so the user gets
   * it from either surface depending on what is on screen.
   */
  readonly triageToast = signal<string | null>(null);
  private triageToastTimer: ReturnType<typeof setTimeout> | null = null;
  private lastTriageNotificationId: number | null = null;

  /**
   * Anchors `triageLanePeers` to the lane the panel was opened in, so
   * walking peers and detecting external moves both key off this rather
   * than the live `selected().info.state` (which can change under us).
   */
  triageLaneState: string | null = null;

  /**
   * Monotonic token guarding the latest `openDetail` request. Bumped on
   * every open/close so a late HTTP reply for a stale job is dropped.
   * Without this the late reply re-sets `selected` and the panel pops
   * back open — visible as a "j to advance, Esc fails to close" race.
   */
  private openDetailToken = 0;

  /**
   * Peers in the same on-disk lane as the currently selected job. The
   * mapping is keyed by the filesystem state on `info.state`; virtual
   * sub-lanes (e.g. `2-ready-intake`) merge back into their parent
   * because they share the same disk lane.
   */
  readonly triageLanePeers = computed<TaskInfo[]>(() => {
    const sel = this.selected();
    if (!sel) return [];
    return this.peersForLane(sel.info.state);
  });

  /**
   * Peers in a specific on-disk lane. Used by `openDetail` to capture
   * the lane-pager snapshot at the moment of click, where `selected` is
   * still the previous detail (or null) and the lookup must key off the
   * incoming job's state, not the stale signal.
   *
   * Reads the project-scoped + faceted-filtered feed (`filteredGrouped`),
   * NOT the raw `jobService.grouped()`. This is the single source of truth
   * the lane-count badge already counts (`displayGrouped` → `filteredGrouped`),
   * so the detail pager's "N / M" total matches the lane badge under every
   * scope/filter combination. Reading the raw feed here was the bug: with a
   * project filter active the pager captured every project's lane peers (e.g.
   * 126) while the badge showed only the scoped subset (e.g. 116).
   */
  peersForLane(state: string): TaskInfo[] {
    const g = this.boardFilters.filteredGrouped();
    // Epics are containers, not board work-items, so the flat lane board hides
    // them (board `excludeEpics`, wired into `displayGrouped`). The pager must
    // drop them too or its "N / M" total would exceed the lane-count badge and
    // Prev/Next could surface an epic that has no card in the lane.
    const tasksOnly = (jobs?: TaskInfo[]): TaskInfo[] =>
      (jobs ?? []).filter((t) => t.kind !== 'epic');
    switch (state) {
      case TaskState.Backlog:          return tasksOnly(g.backlog);
      case TaskState.Preparation:      return tasksOnly(g.preparation);
      case TaskState.OrchestratorPrep: return tasksOnly(g.orchestratorPrep);
      case TaskState.Ready:            return tasksOnly(g.ready);
      case TaskState.Progress:         return tasksOnly(g.progress);
      case TaskState.FailedPickup:     return tasksOnly(g.failedPickup);
      case TaskState.AutoReview:       return tasksOnly(g.autoReview);
      case TaskState.HumanReview:      return tasksOnly(g.humanReview);
      case TaskState.Escalated:        return tasksOnly(g.escalated);
      case TaskState.Completed:        return tasksOnly(g.completed);
      case TaskState.Archive:          return tasksOnly(g.archive);
      default:                       return [];
    }
  }

  isSelected(job: TaskInfo): boolean {
    return this.selected()?.info.taskKey === job.taskKey;
  }

  /** Keep every app-owned task link on the stable key-only URL contract. */
  syncTaskUrl(info: TaskInfo, mode: TaskUrlHistoryMode = 'replace'): boolean {
    const key = taskUrlKey(info);
    if (!key) return false;
    writeTaskUrl(key, mode, this.taskHistoryState());
    return true;
  }

  /** Consume the one-shot tab-reuse intent attached to an in-place task navigation. */
  consumeTaskTabReplacement(taskKey: string): boolean {
    if (this.pendingTaskTabReplacement !== taskKey) return false;
    this.pendingTaskTabReplacement = null;
    return true;
  }

  /**
   * Consume the one reconciliation pass granted to a browser-history restore.
   * The restored detail may now live outside the pager's anchored lane, but
   * that initial mismatch is navigation, not a fresh external lane change.
   * Later changes to the same task must flow through normal reconciliation.
   */
  consumeBrowserHistorySelection(taskKey: string, restoredState: string): boolean {
    if (this.browserHistoryTaskKey !== taskKey) return false;
    this.browserHistoryTaskKey = null;
    // The pager snapshot deliberately remains anchored to the review lane,
    // while external-change detection now compares against the task's lane at
    // the restored point in time. This prevents incidental effect reruns from
    // shrinking the restored pager, yet a later state change still diverges.
    this.triageLaneState = restoredState;
    return true;
  }

  /** Select a detail already fetched by another shell surface. */
  selectResolvedDetail(detail: TaskDetail, mode: TaskUrlHistoryMode = 'push'): void {
    this.cancelRequests();
    this.selectedCore.set(null);
    this.browserHistoryTaskKey = null;
    this.syncTaskUrl(detail.info, mode);
    const token = ++this.openDetailToken;
    this.triageLaneState = detail.info.state;
    this.setSelectedFromAdvance(detail, token);
  }

  private getDetailFor(info: TaskInfo) {
    const project = this.projectLookup.getProjectDisplay(
      info.projectName,
      info.watchPath,
    );
    const handle = project.id ?? project.shortCode ?? project.displayName;
    return this.withDetailTimeout(this.jobService.getDetail(info.id, undefined, handle));
  }

  private projectFor(info: TaskInfo): string {
    const project = this.projectLookup.getProjectDisplay(info.projectName, info.watchPath);
    return project.id ?? project.shortCode ?? project.displayName;
  }

  private cancelRequests(): void {
    for (const request of this.activeRequests) request.unsubscribe();
    this.activeRequests = [];
  }

  private revokeSelection(error: unknown, id: string, watchPath: string): boolean {
    const status = typeof error === 'object' && error !== null && 'status' in error
      ? Number((error as { status: unknown }).status) : 0;
    if (status !== 403 && status !== 404) return false;
    this.prefetch.invalidate(id, watchPath);
    this.cancelRequests();
    this.selectedCore.set(null);
    this.detailPreview.set(null);
    this.selected.set(null);
    this.detailLoading.set(false);
    this.failDetailLoad(error, id, () => this.restoreFromUrl());
    return true;
  }

  private coreInfo(core: TaskCore, board: TaskInfo | null): TaskInfo {
    const base = board ?? {
      id: core.id, taskKey: core.taskKey, watchPath: '', folderPath: '',
      projectName: core.projectName, title: core.title, state: core.lane,
      order: core.order, agent: '', createdAt: '', lastActivity: '',
      sessionName: null, useOwnSession: null, lastUsage: null, execution: null,
      commit: null, model: null, cliType: null,
    } as TaskInfo;
    return {
      ...base, id: core.id, taskKey: core.taskKey, key: core.key,
      title: core.title, projectName: core.projectName, state: core.lane,
      archiveState: core.archiveState, enteredLaneAt: core.enteredLaneAt,
      order: core.order, mode: core.mode as TaskInfo['mode'],
      kind: core.kind as TaskInfo['kind'], released: core.released,
      model: core.pins.model, modelExplicit: core.pins.modelExplicit,
      thinkingLevel: core.pins.thinkingLevel,
      thinkingLevelExplicit: core.pins.thinkingLevelExplicit,
      cliType: core.pins.cliType as TaskInfo['cliType'],
      contextMode: core.pins.contextMode,
      useOwnSession: core.pins.useOwnSession,
      allowWebAccess: core.pins.allowWebAccess,
      noBranchExpected: core.pins.noBranchExpected,
    };
  }

  private startCore(info: TaskInfo, project: string, token: number,
    retry: () => void, replaceTab = false, onAccepted?: (core: TaskCore) => void): void {
    this.cancelRequests();
    this.selectedCore.set(null);
    this.selected.set(null);
    this.detailPreview.set(info);
    this.detailLoading.set(true);
    this.activeProject = project;
    this.activeCoreVersion = null;
    this.activeAttempt = null;
    this.resourceStates.set({
      git: { phase: 'idle', reason: null }, usage: { phase: 'idle', reason: null },
      review: { phase: 'idle', reason: null }, documents: { phase: 'idle', reason: null },
      history: { phase: 'idle', reason: null },
    });
    const accept = (core: TaskCore) => {
      if (token !== this.openDetailToken || core.state === 'warming') return;
      if (core.id !== info.id && core.key !== info.id) return;
      if (info.taskKey && core.taskKey !== info.taskKey) return;
      this.prefetch.storeCore(core, project);
      this.activeProject = core.projectId;
      this.activeCoreVersion = core.coreVersion;
      this.activeAttempt = core.runtime.attemptId;
      this.detailPreview.set(this.coreInfo(core, info));
      this.selectedCore.set(core);
      this.detailLoading.set(false);
      this.clearDetailLoadFailure();
      if (replaceTab) this.pendingTaskTabReplacement = core.taskKey;
      onAccepted?.(core);
      this.markNextTaskRendered();
      perfMark('job-select-rendered');
      perfMeasure('job-select-to-rendered', 'job-select-click', 'job-select-rendered');
      // The bounded text and five timeline events must have a paint opportunity
      // before any document or usage request begins.
      const afterPaint = () => {
        if (token === this.openDetailToken && this.selectedCore()?.coreVersion === core.coreVersion)
          this.loadInitialDocuments(token);
      };
      if (typeof requestAnimationFrame === 'function')
        requestAnimationFrame(() => setTimeout(afterPaint, 0));
      else setTimeout(afterPaint, 0);
    };
    const cached = this.prefetch.takeCore(info.id, project);
    if (cached) accept(cached);
    const request = this.jobService.getCore(info.id, project).pipe(
      timeout({ first: TaskSelectionService.DETAIL_TIMEOUT_MS }),
    ).subscribe({
      next: core => {
        if (token !== this.openDetailToken) return;
        if (core.state === 'warming') {
          this.detailLoading.set(true);
          setTimeout(() => { if (token === this.openDetailToken) this.startCore(info, project, token, retry, replaceTab, onAccepted); }, 600);
          return;
        }
        if (!cached || cached.coreVersion !== core.coreVersion || cached.runtimeVersion !== core.runtimeVersion)
          accept(core);
      },
      error: error => {
        if (token !== this.openDetailToken) return;
        if (this.revokeSelection(error, info.id, info.watchPath)) return;
        if (cached) return;
        this.detailLoading.set(false);
        this.failDetailLoad(error, info.key || info.id, retry);
      },
    });
    this.activeRequests.push(request);
  }

  private resourceMatches<T>(reply: TaskResource<T>, core: TaskCore): boolean {
    return reply.id === core.id && reply.taskKey === core.taskKey
      && reply.projectId === this.activeProject
      && reply.coreVersion === core.coreVersion
      && reply.attemptId === this.activeAttempt;
  }

  private setResourceState(name: ResourceName, phase: ResourcePhase, reason: string | null): void {
    this.resourceStates.update(states => ({ ...states, [name]: { phase, reason } }));
  }

  private loadInitialDocuments(token: number): void {
    const core = this.selectedCore();
    const project = this.activeProject;
    if (!core || !project) return;
    this.setResourceState('documents', 'loading', null);
    const docs: Partial<Record<'prompt' | 'status', TaskDocumentData>> = {};
    let pending = 2;
    const finish = () => {
      if (--pending !== 0 || token !== this.openDetailToken || this.selectedCore() !== core) return;
      const previous = this.selected();
      const info = this.detailPreview() ?? previous?.info;
      if (!info) return;
      const detail = previous ?? emptyDetail(info, core);
      this.selected.set({ ...detail,
        promptMarkdown: docs.prompt?.markdown ?? core.prompt.text,
        statusMarkdown: docs.status?.markdown ?? core.statusSummary.text,
        summaryState: docs.status?.summaryState ?? null });
      this.detailPreview.set(null);
      if (this.resourceStates().documents.phase === 'loading')
        this.setResourceState('documents', 'ready', null);
      // Usage is requested only after the rich view has a paint opportunity.
      requestAnimationFrame(() => setTimeout(() => this.loadResource('usage'), 0));
    };
    for (const name of ['prompt', 'status'] as const) {
      const request = this.jobService.getDetailResource<TaskDocumentData>(core.id, project,
        core.coreVersion, 'documents', name).pipe(
          timeout({ first: TaskSelectionService.DETAIL_TIMEOUT_MS }),
        ).subscribe({
        next: reply => {
          if (token === this.openDetailToken) {
            if (!this.resourceMatches(reply, core))
              this.setResourceState('documents', 'stale', 'core-generation-changed');
            else if (reply.state === 'ready') docs[name] = reply.data;
            else this.setResourceState('documents', reply.state === 'stale' ? 'stale' : 'unavailable', reply.reason);
          }
          finish();
        },
        error: error => {
          if (token === this.openDetailToken && !this.revokeSelection(error, core.id,
            this.detailPreview()?.watchPath ?? ''))
            this.setResourceState('documents', 'error', 'Document request failed');
          finish();
        },
      });
      this.activeRequests.push(request);
    }
  }

  loadResource(name: Exclude<ResourceName, 'documents'>, evidence = false): void {
    const core = this.selectedCore();
    const project = this.activeProject;
    if (!core || !project || this.resourceStates()[name].phase === 'loading') return;
    const token = this.openDetailToken;
    this.setResourceState(name, 'loading', null);
    const request = this.jobService.getDetailResource<TaskUsageData | TaskReviewData | TaskHistoryData | TaskGitData>(
      core.id, project, core.coreVersion, name, undefined, evidence).pipe(
        timeout({ first: TaskSelectionService.DETAIL_TIMEOUT_MS }),
      ).subscribe({
      next: reply => {
        if (token !== this.openDetailToken) return;
        if (!this.resourceMatches(reply, core)) {
          this.setResourceState(name, 'stale', 'core-generation-changed');
          return;
        }
        this.setResourceState(name, reply.state === 'ready' ? 'ready' : reply.state,
          reply.reason);
        const detail = this.selected();
        if (!detail) return;
        if (name === 'review') {
          const data = reply.data as TaskReviewData;
          this.selected.set({ ...detail,
            info: reply.state === 'ready' && data.reviewProjection
              ? { ...detail.info, reviewProjection: data.reviewProjection } : detail.info,
            reviewEvidence: data.evidence ?? detail.reviewEvidence });
          return;
        }
        if (name === 'git') {
          const data = reply.data as TaskGitData;
          this.selected.set({ ...detail, info: reply.state === 'ready'
            ? { ...detail.info, ...data }
            : { ...detail.info, commit: data.commit, commits: data.commits } });
          return;
        }
        if (reply.state !== 'ready') return;
        if (name === 'usage') {
          const data = reply.data as TaskUsageData;
          this.selected.set({ ...detail, contextUsage: data.contextUsage,
            info: { ...detail.info,
              tokenSummary: data.tokenSummary ?? null,
              lastUsage: data.lastUsage ?? null } });
        }
        if (name === 'history') {
          const data = reply.data as TaskHistoryData;
          this.selected.set({ ...detail, promptHistory: data.promptHistory,
            titleHistory: data.titleHistory, log: data.log });
        }
      },
      error: error => {
        if (token === this.openDetailToken && !this.revokeSelection(error, core.id,
          this.selected()?.info.watchPath ?? this.detailPreview()?.watchPath ?? '')) {
          const status = typeof error === 'object' && error !== null && 'status' in error
            ? Number((error as { status: unknown }).status) : 0;
          this.setResourceState(name, status === 409 ? 'stale' : 'error',
            status === 409 ? 'core-generation-changed' : `${name} request failed`);
        }
      },
    });
    this.activeRequests.push(request);
  }

  retryDocuments(): void {
    if (this.selectedCore()) this.loadInitialDocuments(this.openDetailToken);
  }

  private withDetailTimeout(request: Observable<TaskDetail>): Observable<TaskDetail> {
    return request.pipe(timeout({ first: TaskSelectionService.DETAIL_TIMEOUT_MS }));
  }

  /**
   * Resolve a persisted composite key's storage reference to registry
   * identity. Older search snapshots may include the lane directory below
   * the project root, so use the longest containing registry path. The
   * storage reference is never sent to the detail endpoint.
   */
  private projectHandleForStorageReference(storageReference: string): string | undefined {
    const normalize = (value: string) =>
      value.replace(/[\\/]+/g, '/').replace(/\/+$/, '').toLowerCase();
    const reference = normalize(storageReference);
    const project = [...this.projectLookup.allProjects()]
      .filter(candidate => {
        const storage = normalize(candidate.storageLocation);
        return reference === storage || reference.startsWith(`${storage}/`);
      })
      .sort((left, right) => right.storageLocation.length - left.storageLocation.length)[0];
    return project?.id ?? project?.shortCode ?? project?.displayName;
  }

  private projectForPublicReference(reference: string): { id: string; storageLocation: string; displayName: string } | null {
    const prefix = reference.slice(0, reference.lastIndexOf('-')).toLowerCase();
    const projects = [...this.projectLookup.allProjects()];
    return projects.find(project => project.shortCode?.toLowerCase() === prefix)
      ?? (projects.length === 1 ? projects[0] : null);
  }

  /**
   * Board and Explorer entry point. Publishes the cheap route shell now and
   * starts detail work only after the browser has had a frame to paint it.
   */
  openDetailAfterPaint(job: TaskInfo): void {
    const previewToken = ++this.openDetailToken;
    this.prepareDetailLoad(() => this.openDetailAfterPaint(job));
    this.detailPreview.set(job);
    this.detailLoading.set(true);
    const start = () => {
      if (previewToken !== this.openDetailToken) return;
      this.openDetail(job);
    };
    if (typeof requestAnimationFrame !== 'function') {
      start();
      return;
    }
    requestAnimationFrame(() => setTimeout(start, 0));
  }

  /**
   * Open the side panel for `job`. Updates URL + fetches detail. By
   * default captures a fresh lane-pager snapshot anchored on `job` —
   * pass `{ keepPagerSnapshot: true }` from the pager step itself so
   * the in-progress iteration is preserved rather than re-captured.
   */
  openDetail(job: TaskInfo, opts: { keepPagerSnapshot?: boolean } = {}): void {
    this.browserHistoryTaskKey = null;
    this.pendingCoreReference = null;
    // Step 1 of the perf-baseline contract: job-select click span. The
    // accept-to-next-task pipeline owns its own marks via markAcceptClick;
    // this one covers ad-hoc board clicks where no accept-click preceded.
    perfMark('job-select-click');
    this.prepareDetailLoad(() => this.openDetail(job, opts));
    // Paint the task shell from the already-resident board record. The heavy
    // detail request and all child-section requests can now run after the
    // route is visible instead of holding the user on the board.
    this.detailPreview.set(job);
    this.triageLaneState = job.state;
    if (!opts.keepPagerSnapshot) {
      // Capture peers for `job.state` directly: at this point `selected`
      // may still be null or pointing at a prior detail in a different
      // lane, so the `triageLanePeers` computed (which keys off
      // `selected.info.state`) would yield the wrong list or an empty
      // list. `peersForLane` looks up the live grouped lane.
      this.pager.capture(job.state, this.peersForLane(job.state), job.taskKey);
    }
    this.syncTaskUrl(job, 'push');
    const token = ++this.openDetailToken;
    this.startCore(job, this.projectFor(job), token, () => this.openDetail(job, opts));
  }

  /**
   * Lane dropdown navigation (ASS-661): re-point the pager at `state` and
   * open a task in it. The dropdown is navigation-only — it never moves the
   * current task; it only chooses which lane Prev/Next pages through.
   *
   *   - Empty lane  → toast and stay put (the snapshot keeps its lane).
   *   - Current task already lives in `state` → anchor on it (no jump).
   *   - Otherwise   → open the lane's first task.
   *
   * `openDetail` captures a fresh snapshot for the landed task's lane
   * synchronously, so callers that read the pager-lane signal right after
   * this returns see the new lane (used to re-sync the native <select>).
   */
  navigateToLane(state: string): void {
    const peers = this.peersForLane(state);
    if (peers.length === 0) {
      this.showTriageToast(`No tasks in ${laneLabelFor(state)}.`);
      return;
    }
    const sel = this.selected();
    const onCurrent = sel && peers.find(p => p.taskKey === sel.info.taskKey);
    this.openDetail(onCurrent ?? peers[0]);
  }

  /**
   * Pager Prev / Next: step the snapshot's index and fetch the detail
   * at the new position. The snapshot is preserved (we don't re-capture
   * from the current live lane), so a status change on the previously
   * visible job does not break the iteration order. Returns true when
   * a step actually happened.
   */
  pagerStep(direction: -1 | 1): boolean {
    this.persistCurrentPagerInHistory();
    const entry = this.pager.step(direction);
    if (!entry) return false;
    this.loadPagerEntry(entry);
    return true;
  }

  private loadPagerEntry(entry: LanePagerEntry): void {
    this.browserHistoryTaskKey = null;
    this.prepareDetailLoad(() => this.loadPagerEntry(entry));
    if (entry.routeKey) writeTaskUrl(entry.routeKey, 'push', this.taskHistoryState());
    const token = ++this.openDetailToken;
    const live = this.jobService.jobs().find(task => task.taskKey === entry.taskKey);
    const project = live ? this.projectFor(live) : this.projectHandleForStorageReference(entry.watchPath);
    if (!project) {
      this.failDetailLoad(null, entry.routeKey || entry.id, () => this.loadPagerEntry(entry));
      return;
    }
    const info = live ?? { id: entry.id, key: entry.routeKey, taskKey: entry.taskKey,
      watchPath: entry.watchPath, projectName: project, state: this.pager.snapshot()?.lane ?? '',
      title: entry.title ?? entry.id } as TaskInfo;
    this.triageLaneState = this.pager.snapshot()?.lane ?? info.state;
    this.startCore(info, project, token, () => this.loadPagerEntry(entry), true);
  }

  closeDetail(): void {
    // Bump the token so any in-flight `openDetail` reply (e.g. user
    // pressed `j` then immediately Esc) drops its `selected.set` and
    // the panel does not pop back open after we close it.
    this.openDetailToken++;
    this.cancelRequests();
    this.selectedCore.set(null);
    this.detailLoading.set(false);
    this.clearDetailLoadFailure();
    this.detailPreview.set(null);
    this.selected.set(null);
    this.triageLaneState = null;
    this.browserHistoryTaskKey = null;
    this.pendingCoreReference = null;
    this.pager.clear();
    clearTaskUrl('push');
  }

  /**
   * Studio-shell reload survival: hydrate `selected` from a persisted
   * task tab's `taskKey` (`<watchPath>::<id>`) without needing the board
   * to have loaded `jobs()` first. Used by the shell's active-tab→selection
   * sync effect so that on a cold reload the restored active task tab
   * paints its detail instead of the "No task selected" placeholder.
   *
   * On success the composite internal key is projected to the public
   * `#/tasks/<AGT-NNN>` route. The watch path never enters browser history.
   */
  openDetailByTaskKey(taskKey: string): void {
    if (this.browserHistoryTaskKey !== taskKey) this.browserHistoryTaskKey = null;
    const liveInfo = this.jobService.jobs().find(task => task.taskKey === taskKey);
    const sep = taskKey.lastIndexOf('::');
    if (!liveInfo && sep < 0) {
      this.detailLoading.set(false);
      this.failDetailLoad(null, taskKey, () => this.openDetailByTaskKey(taskKey));
      return;
    }
    const storageReference = taskKey.slice(0, sep);
    const jobId = taskKey.slice(sep + 2);
    if (!liveInfo && (!jobId || !storageReference)) {
      this.detailLoading.set(false);
      this.failDetailLoad(null, taskKey, () => this.openDetailByTaskKey(taskKey));
      return;
    }
    const label = liveInfo?.key || liveInfo?.id || jobId;
    const handle = liveInfo ? this.projectFor(liveInfo)
      : this.projectHandleForStorageReference(storageReference);
    if (handle) {
      this.prepareDetailLoad(() => this.openDetailByTaskKey(taskKey));
      const token = ++this.openDetailToken;
      const info = liveInfo ?? { id: jobId, taskKey: '', key: null,
        watchPath: storageReference, projectName: handle, state: '', title: jobId } as TaskInfo;
      this.startCore(info, handle, token, () => this.openDetailByTaskKey(taskKey), false,
        core => { this.syncTaskUrl(this.coreInfo(core, info), 'replace'); this.triageLaneState = core.lane; });
      return;
    }
    this.prepareDetailLoad(() => this.openDetailByTaskKey(taskKey));
    if (liveInfo) this.detailPreview.set(liveInfo);
    this.detailLoading.set(true);
    const token = ++this.openDetailToken;
    const request = liveInfo
      ? this.getDetailFor(liveInfo)
      : this.withDetailTimeout(this.jobService.getDetail(
          jobId,
          undefined,
          this.projectHandleForStorageReference(storageReference),
        ));
    request.subscribe({
      next: (detail) => {
        if (token !== this.openDetailToken) return;
        this.detailLoading.set(false);
        this.clearDetailLoadFailure();
        this.syncTaskUrl(detail.info, 'replace');
        this.selected.set(detail);
        this.detailPreview.set(null);
        this.triageLaneState = detail.info.state;
      },
      error: (err) => {
        if (token !== this.openDetailToken) return;
        this.detailLoading.set(false);
        this.failDetailLoad(err, label, () => this.openDetailByTaskKey(taskKey));
      },
    });
  }

  /**
   * Studio-shell tab switch away from a task (board / project / hub / diff
   * / activity tab becomes active): drop the selection and strip stale task
   * route params so a subsequent F5 restores the *current* view rather than
   * re-opening the last task detail. Unlike `closeDetail`, this preserves
   * any hash-based overlay route in the URL.
   */
  clearSelectionForTabSwitch(): void {
    this.openDetailToken++;
    this.cancelRequests();
    this.selectedCore.set(null);
    this.detailLoading.set(false);
    this.clearDetailLoadFailure();
    this.detailPreview.set(null);
    this.selected.set(null);
    this.triageLaneState = null;
    this.browserHistoryTaskKey = null;
    this.pendingCoreReference = null;
    this.pager.clear();
    this.clearTaskParamsFromUrl();
  }

  /** Strip task routing from the canonical hash or legacy query. */
  private clearTaskParamsFromUrl(): void {
    clearTaskUrl('replace');
  }

  /**
   * Reload and browser-history survival. `#/tasks/<key>` is canonical and is
   * resolved without a watch path. The legacy `?task=<key>` and
   * `?job=<slug>&watchPath=<path>` shapes remain readable, but a successful
   * lookup replaces them with the canonical key URL so the local path is not
   * retained in history.
   */
  restoreFromUrl(fromPopState = false): void {
    const currentUrl = new URL(window.location.href);
    const params = currentUrl.searchParams;
    const taskReference = taskReferenceFromUrl(currentUrl);
    const legacyJobId = params.get('job')?.trim() || null;
    const legacyWatchPath = params.get('watchPath')?.trim() || null;
    const legacy = !taskReference && !!legacyJobId;
    const canonicalWithLegacyResidue = !!taskReference && (!!legacyJobId || !!legacyWatchPath);

    if (fromPopState) {
      const restoredPager = this.pagerSnapshotFromHistory();
      if (restoredPager !== undefined) {
        if (restoredPager) this.pager.restore(restoredPager);
        else this.pager.clear();
      }
    }

    if (!taskReference && !legacyJobId) {
      this.pendingCoreReference = null;
      if (fromPopState) {
        this.openDetailToken++;
        this.cancelRequests();
        this.selectedCore.set(null);
        this.detailLoading.set(false);
        this.detailPreview.set(null);
        this.selected.set(null);
        this.triageLaneState = null;
        this.browserHistoryTaskKey = null;
        this.browserRouteCleared.update(value => value + 1);
      }
      this.pager.clear();
      return;
    }

    const token = ++this.openDetailToken;
    this.prepareDetailLoad(() => this.restoreFromUrl(fromPopState));
    this.detailLoading.set(true);
    if (taskReference) {
      const project = this.projectForPublicReference(taskReference);
      if (project) {
        this.pendingCoreReference = null;
        const info = { id: taskReference, taskKey: '', key: taskReference,
          watchPath: project.storageLocation, projectName: project.displayName,
          state: '', title: taskReference } as TaskInfo;
        this.startCore(info, project.id, token, () => this.restoreFromUrl(fromPopState),
          fromPopState, core => {
            const resolved = this.coreInfo(core, info);
            if (canonicalWithLegacyResidue) this.syncTaskUrl(resolved, 'replace');
            if (fromPopState) this.browserHistoryTaskKey = core.taskKey;
            this.triageLaneState = this.pager.snapshot()?.lane ?? core.lane;
          });
        return;
      }
      if (this.projectLookup.allProjects().length > 0) {
        this.detailLoading.set(false);
        this.failDetailLoad(null, taskReference,
          () => this.restoreFromUrl(fromPopState));
        return;
      }
      this.pendingCoreReference = taskReference;
      return;
    }
    const request = taskReference
      ? this.withDetailTimeout(this.jobService.getDetail(taskReference))
      : this.withDetailTimeout(this.jobService.getDetail(legacyJobId!, legacyWatchPath ?? undefined));

    request.subscribe({
      next: (detail) => {
        if (token !== this.openDetailToken) return;
        this.detailLoading.set(false);
        this.clearDetailLoadFailure();
        // A clean canonical URL is deliberately left byte-for-byte untouched.
        // Legacy locators are redirected once, and mixed URLs are scrubbed
        // after the server proves which stable key owns the reference.
        if (legacy || canonicalWithLegacyResidue) this.syncTaskUrl(detail.info, 'replace');
        if (fromPopState) {
          this.pendingTaskTabReplacement = detail.info.taskKey;
          this.browserHistoryTaskKey = detail.info.taskKey;
        } else {
          this.browserHistoryTaskKey = null;
        }
        this.selected.set(detail);

        const snap = this.pager.snapshot();
        if (snap && snap.jobs.some(j => j.taskKey === detail.info.taskKey)) {
          this.triageLaneState = snap.lane;
          this.pager.reanchorTo(detail.info.taskKey);
        } else {
          if (snap) this.pager.clear();
          this.triageLaneState = detail.info.state;
        }
      },
      error: (err) => {
        if (token !== this.openDetailToken) return;
        this.detailLoading.set(false);
        this.selected.set(null);
        this.triageLaneState = null;
        this.browserHistoryTaskKey = null;
        this.failDetailLoad(err, taskReference || legacyJobId || 'task', () => this.restoreFromUrl(fromPopState));
      },
    });
  }

  /**
   * Used by `advanceToNextInLane` in the shell to land on the next
   * peer without touching the URL via `openDetail` (which would
   * publish an intermediate state). Caller is responsible for the
   * URL update + token check; we just set the signal.
   */
  setSelectedFromAdvance(
    detail: TaskDetail,
    expectedToken: number,
    replaceCurrentTaskTab = false,
  ): void {
    if (expectedToken !== this.openDetailToken) return;
    this.cancelRequests();
    this.selectedCore.set(null);
    if (this.browserHistoryTaskKey !== detail.info.taskKey) this.browserHistoryTaskKey = null;
    if (replaceCurrentTaskTab) this.pendingTaskTabReplacement = detail.info.taskKey;
    this.selected.set(detail);
    this.markNextTaskRendered();
  }

  /**
   * After a user-initiated mutation removes the currently visible job
   * from the lane (delete from detail, lane change via state dropdown,
   * triage move/delete): drop the job from the pager snapshot and
   * navigate to the entry that now sits at its slot. When the lane is
   * empty (the job was the last in the iteration) close the panel and
   * surface a "Lane cleared." toast so the user knows the iteration
   * finished.
   *
   * Returns `true` when an advance happened (the panel now shows the
   * next job), `false` when the lane was cleared or there is no active
   * pager snapshot (e.g. the detail was opened from a deep-link with
   * no preceding board click); in the latter case the caller's default
   * post-mutation behaviour - usually `closeDetail()` - still applies.
   */
  advanceAfterMutation(departingJobKey: string): boolean {
    const snapBefore = this.pager.snapshot();
    const wasInSnapshot = !!snapBefore && snapBefore.jobs.some(j => j.taskKey === departingJobKey);
    if (wasInSnapshot) this.persistCurrentPagerInHistory();
    const entry = this.pager.removeAndAdvance(departingJobKey);
    if (!entry) {
      if (wasInSnapshot) {
        // Snapshot existed and the departing job was in it: removeAndAdvance
        // cleared the iteration. Close the panel and toast so the user knows
        // they finished the lane.
        this.closeDetail();
        this.showTriageToast('Lane cleared.');
        return true;
      }
      return false;
    }
    this.triageLaneState = this.pager.snapshot()?.lane ?? this.triageLaneState;
    this.loadAdvancedEntry(entry);
    return true;
  }

  private loadAdvancedEntry(entry: LanePagerEntry): void {
    this.browserHistoryTaskKey = null;
    this.prepareDetailLoad(() => this.loadAdvancedEntry(entry));
    if (entry.routeKey) writeTaskUrl(entry.routeKey, 'push', this.taskHistoryState());
    const token = ++this.openDetailToken;
    const live = this.jobService.jobs().find(task => task.taskKey === entry.taskKey);
    const project = live ? this.projectFor(live) : this.projectHandleForStorageReference(entry.watchPath);
    if (!project) {
      this.failDetailLoad(null, entry.routeKey || entry.id, () => this.loadAdvancedEntry(entry));
      return;
    }
    const info = live ?? { id: entry.id, key: entry.routeKey, taskKey: entry.taskKey,
      watchPath: entry.watchPath, projectName: project, state: this.pager.snapshot()?.lane ?? '',
      title: entry.title ?? entry.id } as TaskInfo;
    this.startCore(info, project, token, () => this.loadAdvancedEntry(entry), true);
  }

  retryDetailLoad(): void {
    this.detailLoadRetry?.();
  }

  private prepareDetailLoad(retry: () => void): void {
    this.detailLoadRetry = retry;
    this.detailLoadError.set(null);
  }

  private taskHistoryState(): TaskBrowserHistoryState {
    if (typeof history === 'undefined') {
      return { [TASK_PAGER_HISTORY_STATE]: this.pager.snapshot() };
    }
    const current = history.state;
    const base = current && typeof current === 'object' ? current as Record<string, unknown> : {};
    return { ...base, [TASK_PAGER_HISTORY_STATE]: this.pager.snapshot() };
  }

  private persistCurrentPagerInHistory(): void {
    const current = this.selected();
    if (current) this.syncTaskUrl(current.info, 'replace');
  }

  private pagerSnapshotFromHistory(): LanePagerSnapshot | null | undefined {
    if (typeof history === 'undefined') return undefined;
    const current = history.state;
    if (!current || typeof current !== 'object'
      || !(TASK_PAGER_HISTORY_STATE in current)) return undefined;
    return (current as TaskBrowserHistoryState)[TASK_PAGER_HISTORY_STATE] ?? null;
  }

  private clearDetailLoadFailure(): void {
    this.detailLoadRetry = null;
    this.detailLoadError.set(null);
  }

  private failDetailLoad(error: unknown, taskLabel: string, retry: () => void): void {
    const status = typeof error === 'object' && error !== null && 'status' in error
      ? Number((error as { status?: unknown }).status)
      : 0;
    this.detailLoadRetry = retry;
    const timedOut = typeof error === 'object' && error !== null
      && 'name' in error && (error as { name?: unknown }).name === 'TimeoutError';
    this.detailLoadError.set({
      taskLabel,
      message: timedOut
        ? 'The detail request timed out. Retry to request the sections again.'
        : status === 404
        ? 'The task reference is no longer current. Retry to resolve its latest location.'
        : 'The detail request failed. Check the connection and try again.',
    });
  }

  /** Bumps the request token and returns the new value. Use from advance handlers. */
  bumpOpenDetailToken(): number {
    return ++this.openDetailToken;
  }

  /**
   * Show a transient triage banner; auto-clears after `durationMs`.
   * Also raises a unified `info` notification so the same outcome shows
   * up in the shared notification stack — keeping the look consistent
   * with other app-wide feedback while the detail-anchored banner stays
   * for users focused on the panel.
   */
  showTriageToast(msg: string, durationMs = 3000): void {
    if (this.triageToastTimer) clearTimeout(this.triageToastTimer);
    this.triageToast.set(msg);
    this.triageToastTimer = setTimeout(() => {
      this.triageToast.set(null);
      this.triageToastTimer = null;
    }, durationMs);
    if (this.lastTriageNotificationId != null) {
      this.notifications.dismiss(this.lastTriageNotificationId);
    }
    this.lastTriageNotificationId = this.notifications.notify({
      message: msg,
      kind: 'info',
      durationMs,
    });
  }
}
