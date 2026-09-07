import { Injectable, inject } from '@angular/core';
import { boardLanes, TaskInfo } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import { ConfirmDialogService } from '../../../services/confirm-dialog.service';
import { UndoController } from '../../../services/undo.service';
import { TaskDetailPrefetchService } from './task-detail-prefetch.service';
import { TaskSelectionService } from './task-selection.service';
import { LanePagerService, LANE_LABELS } from './lane-pager.service';
import {
  laneLabelFor,
  needsPlanningAcceptWarning,
  needsUnintegratedArchiveWarning,
} from './triage-actions.model';

/**
 * Cycle 10c job-detail-feature controller: orchestrates the triage
 * panel's move / move-to-top / delete / start actions, the j/k peer
 * navigation, and the auto-advance-after-mutation flow that walks the
 * user to the next job in the same lane.
 *
 * Bridges to the shell's `TaskDetailComponent` ViewChild (where
 * `triageActing` lives) via a tiny callback the shell registers in
 * `ngAfterViewInit`. Avoids a hard dependency on the component class
 * here — the controller is a pure state-machine + TaskService caller.
 *
 * The auto-advance EFFECT stays in the shell because it needs to
 * read `jobDetailRef.triageActingId()` reactively; the effect calls
 * `advanceToNextInLane` here when the conditions match.
 */
@Injectable({ providedIn: 'root' })
export class TriageController {
  private readonly jobService = inject(TaskService);
  private readonly errorDialog = inject(ErrorDialogService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  private readonly jobSelection = inject(TaskSelectionService);
  private readonly lanePager = inject(LanePagerService);
  private readonly prefetch = inject(TaskDetailPrefetchService);
  private readonly undo = inject(UndoController);

  /**
   * Invoked at every triage decision so the TaskDetailComponent's
   * "acting" highlight clears even when the move/delete fails. The
   * shell registers this callback once in `ngAfterViewInit`.
   */
  private clearActingCallback: (() => void) | null = null;

  setClearActingCallback(fn: (() => void) | null): void {
    this.clearActingCallback = fn;
  }

  private clearActing(): void {
    this.clearActingCallback?.();
  }

  // ---------- lane-action triage actions ----------

  /**
   * Lane-specific move from the triage panel. Optimistic-everything:
   *
   *   1. Mark the click for the `accept-to-next-task` performance
   *      interval (paired with `markNextTaskRendered` on the selection
   *      service when the new panel paints).
   *   2. Repaint the board's lane immediately
   *      (`applyOptimisticMove`).
   *   3. Navigate to the next peer **before** the POST has returned
   *      (`advanceAfterMutation` consumes prefetched detail
   *      synchronously; falls back to the live lane peers).
   *   4. Fire the move POST in parallel. On 5xx / 4xx the optimistic
   *      reorder + the panel navigation both revert, an error toast
   *      surfaces, and the user lands back on the original job.
   *
   * The previous shape did 1 then 2 then awaited the POST before step
   * 3, so the user paid both the move POST + the next-task GET in
   * series. With the lane-pager prefetch already warming the next
   * peer's detail, step 3 is now a synchronous signal flip.
   */
  /**
   * Lane-specific move. AGT-2069: accepting a planning task (move to
   * 6-completed) first passes through the spawn-contract guard — if the task
   * spawned no follow-up cards and carries no "no follow-up intended"
   * declaration, a confirm dialog surfaces the AGT-1915 trap and the operator
   * must explicitly accept anyway. Every other move goes straight through.
   */
  move(info: TaskInfo, ev: { targetState: string; actionId: string }): void {
    if (needsUnintegratedArchiveWarning(info, ev.targetState)) {
      void this.confirmUnintegratedArchiveThenMove(info, ev);
      return;
    }
    if (needsPlanningAcceptWarning(info, ev.targetState)) {
      void this.confirmPlanningAcceptThenMove(info, ev);
      return;
    }
    this.performMove(info, ev);
  }

  private async confirmUnintegratedArchiveThenMove(
    info: TaskInfo,
    ev: { targetState: string; actionId: string },
  ): Promise<void> {
    const integration = info.integration;
    const status = integration?.status ?? 'unknown';
    const branch = integration?.integrationBranch || 'develop';
    const ok = await this.confirmDialog.confirm({
      title: 'Archive before integration?',
      message:
        `This task is not integrated into ${branch} (status: ${status}). ` +
        'Archiving keeps the task and its evidence, but moves the unresolved integration state out of Delivered.',
      detail: integration?.detail || info.title || info.id,
      confirmLabel: 'Archive anyway',
      cancelLabel: 'Keep in Delivered',
      kind: 'primary',
    });
    if (!ok) {
      this.clearActing();
      return;
    }
    this.performMove(info, ev);
  }

  private async confirmPlanningAcceptThenMove(
    info: TaskInfo,
    ev: { targetState: string; actionId: string },
  ): Promise<void> {
    const ok = await this.confirmDialog.confirm({
      title: 'Planning task without follow-up cards',
      message:
        'This planning task has not spawned any follow-up cards, and no "no follow-up intended" ' +
        'declaration was made. Accepting it now risks the AGT-1915 trap — a plan approved with no ' +
        'work ever created. Accept anyway?',
      detail: info.title || info.id,
      confirmLabel: 'Accept anyway',
      cancelLabel: 'Keep in review',
      kind: 'danger',
    });
    if (!ok) {
      this.clearActing();
      return;
    }
    this.performMove(info, ev);
  }

  private performMove(info: TaskInfo, ev: { targetState: string; actionId: string }): void {
    const lane = this.jobSelection.triageLaneState ?? info.state;
    const peers = this.jobSelection.triageLanePeers();
    // Capture prev lane + slot BEFORE the optimistic move so undo can
    // restore the card to its exact origin position.
    const prevState = info.state;
    const snapshotIndex = this.jobService.findLaneIndex(info.id, info.watchPath, prevState);
    const peerIndex = peers.findIndex(peer => peer.taskKey === info.taskKey);
    const prevIndex = snapshotIndex >= 0 ? snapshotIndex : Math.max(peerIndex, 0);
    this.jobSelection.markAcceptClick();
    const snapshot = this.jobService.applyOptimisticMove(info.id, info.watchPath, ev.targetState);
    this.jobService.beginOptimisticPersist();

    // The optimistic detail for the departing job is stale the moment
    // we move it; drop the cache entry so the next click-back doesn't
    // serve a pre-move snapshot.
    this.prefetch.invalidate(info.id, info.watchPath);

    // Optimistic navigation: advance to the next peer right now, while
    // the POST is still on the wire. advanceAfterMutation uses the
    // pager snapshot (the lane iteration the user opened detail in)
    // and the prefetch cache, so the new panel paints without a
    // roundtrip.
    let advanced = false;
    if (this.jobSelection.advanceAfterMutation(info.taskKey)) {
      advanced = true;
    } else if (this.advanceToNextInLane(lane, info.taskKey, peers)) {
      advanced = true;
    }

    let persistResolve!: () => void;
    let persistReject!: (reason: unknown) => void;
    const persisted = new Promise<void>((resolve, reject) => {
      persistResolve = resolve;
      persistReject = reject;
    });
    void persisted.catch(() => undefined);

    const actionLabel = ev.targetState === '6-completed'
        ? 'Accepted'
        : ev.targetState === '2-ready'
          ? 'Requeued'
          : ev.targetState === '7-archive' ? 'Archived' : 'Moved';
    this.undo.offerLaneRevert({
      jobId: info.id,
      watchPath: info.watchPath,
      jobLabel: info.title || info.id,
      actionLabel,
      targetLaneLabel: laneLabelFor(ev.targetState),
      prevState,
      prevIndex,
      persisted,
    });

    this.jobService.moveJob(info.id, ev.targetState, info.watchPath).subscribe({
      next: () => {
        this.jobService.endOptimisticPersist();
        persistResolve();
        this.clearActing();
      },
      error: (err) => {
        this.jobService.endOptimisticPersist();
        persistReject(err);
        this.undo.cancelActive();
        if (snapshot) this.jobService.revertOptimisticMove(snapshot);
        // Optimistic navigation must roll back too: the user clicked
        // Accept on `info`, the move failed, the only sensible landing
        // spot is the job they tried to act on.
        if (advanced) this.jobSelection.openDetail(info);
        this.clearActing();
        this.errorDialog.show(err, {
          title: 'Failed to move task',
          fallbackMessage: 'Failed to move task',
          source: `Task ${info.id}`,
        });
      },
    });
  }

  /** Triage "Move to top" (only on 2-ready). Stays in lane; clears acting. */
  moveToTop(info: TaskInfo, ev: { actionId: string }): void {
    void ev;
    // Capture the lane's exact ordered list BEFORE the promote so undo
    // can replay it via /api/tasks/reorder. Same-lane reorders cannot be
    // expressed via the cross-lane move endpoint (it skips
    // SetOrderInLane when fromState == targetState), so we replay the
    // full order.
    const prevOrder = this.captureLaneOrder(info.state);
    this.jobService.beginOptimisticPersist();
    this.jobService.moveJobToTop(info.id, info.watchPath).subscribe({
      next: () => {
        this.jobService.endOptimisticPersist();
        if (prevOrder.length > 0) {
          this.undo.offerReorderRevert({
            jobId: info.id,
            jobLabel: info.title || info.id,
            actionLabel: 'Moved',
            targetLaneLabel: `top of ${laneLabelFor(info.state)}`,
            laneState: info.state,
            prevOrder,
          });
        }
        this.clearActing();
      },
      error: (err) => {
        this.jobService.endOptimisticPersist();
        this.clearActing();
        this.errorDialog.show(err, {
          title: 'Failed to move task to top',
          fallbackMessage: 'Failed to move task to the top of the Ready queue',
          source: `Task ${info.id}`,
        });
      },
    });
  }

  /**
   * Triage "Delete". Confirm-on-first-click already happened in the
   * panel; we still surface the unified confirm dialog to match the
   * menu's delete flow (so the user does not lose the safety net by
   * accident).
   */
  async delete(info: TaskInfo, ev: { actionId: string }): Promise<void> {
    void ev;
    const lane = this.jobSelection.triageLaneState ?? info.state;
    const peers = this.jobSelection.triageLanePeers();
    const label = info.title || info.id;
    const ok = await this.confirmDialog.confirm({
      title: 'Delete this task?',
      message: 'This removes the job folder and all its files (prompt, logs, results). Do you really want this?',
      detail: label,
      confirmLabel: 'Delete',
      cancelLabel: 'Keep',
      kind: 'danger',
    });
    if (!ok) {
      this.clearActing();
      return;
    }
    this.jobService.deleteJob(info.id, info.watchPath).subscribe({
      next: () => {
        this.jobService.refresh();
        this.clearActing();
        // Prefer the lane-pager snapshot when one is active so the
        // iteration count and URL update consistently with the
        // detail-header Delete and state-dropdown paths.
        if (this.jobSelection.advanceAfterMutation(info.taskKey)) return;
        this.advanceToNextInLane(lane, info.taskKey, peers);
      },
      error: (err) => {
        this.clearActing();
        this.errorDialog.show(err, {
          title: 'Failed to delete task',
          fallbackMessage: 'Failed to delete task',
          source: `Task ${info.id}`,
        });
      },
    });
  }

  /**
   * Triage "Run now": kick the start path then leave the panel on the
   * same job (it will transition to 3-progress on its own).
   */
  start(info: TaskInfo, ev: { actionId: string }): void {
    void ev;
    this.jobService.startJob(info.id, info.watchPath).subscribe({
      next: () => this.clearActing(),
      error: (err) => {
        this.clearActing();
        this.errorDialog.show(err, {
          title: 'Failed to start task',
          fallbackMessage: 'Failed to start task',
          source: `Task ${info.id}`,
        });
      },
    });
  }

  /**
   * Snapshot the current ordered list of `{jobId, watchPath}` in the
   * given state. Returns an empty array when the lane is unknown or
   * empty. Used by the undo flow to replay a pre-action order via
   * `POST /api/tasks/reorder`.
   */
  private captureLaneOrder(state: string): { jobId: string; watchPath: string }[] {
    const grouped = this.jobService.grouped();
    // The triage panel sees one of seven lanes; pick the matching list
    // by scanning the grouped buckets for jobs whose `state` matches.
    const found: { jobId: string; watchPath: string }[] = [];
    for (const list of boardLanes(grouped)) {
      for (const j of list) {
        if (j.state === state) found.push({ jobId: j.id, watchPath: j.watchPath });
      }
    }
    return found;
  }

  // ---------- peer navigation (j / k / arrows / pager buttons) ----------

  /**
   * j / ↓ / → / pager-next: advance through the snapshot captured when
   * the user entered the detail view. The snapshot is intentionally
   * stable - a status change on the currently visible job preserves its
   * slot in the iteration, so this call still lands on the next slug
   * captured at entry time, not on whatever live ordering shows now.
   *
   * Falls back to the live lane peers when no snapshot is available
   * (e.g. detail opened from a URL with no prior iteration).
   */
  next(info: TaskInfo): boolean {
    if (this.jobSelection.pagerStep(1)) return true;
    const peers = this.jobSelection.triageLanePeers();
    if (peers.length === 0) return false;
    const idx = peers.findIndex((p) => p.taskKey === info.taskKey);
    const nextIdx = idx < 0 ? 0 : Math.min(peers.length - 1, idx + 1);
    if (nextIdx === idx) return false;
    this.jobSelection.openDetail(peers[nextIdx]);
    return true;
  }

  /** k / ↑ / ← / pager-prev: see `next` - same snapshot semantics. */
  prev(info: TaskInfo): boolean {
    if (this.jobSelection.pagerStep(-1)) return true;
    const peers = this.jobSelection.triageLanePeers();
    if (peers.length === 0) return false;
    const idx = peers.findIndex((p) => p.taskKey === info.taskKey);
    const prevIdx = idx < 0 ? 0 : Math.max(0, idx - 1);
    if (prevIdx === idx) return false;
    this.jobSelection.openDetail(peers[prevIdx]);
    return true;
  }

  // ---------- auto-advance after mutation / external move ----------

  /**
   * After a triage decision (or external move), find the next peer in
   * the lane the user was triaging in, excluding the job that just
   * left. If the lane is empty, close the panel and toast.
   *
   * `external` flips the toast wording so the user can tell whether
   * the advance was their click or someone else's reshuffle.
   */
  advanceToNextInLane(
    lane: string,
    departingJobKey: string,
    peersBefore: readonly TaskInfo[],
    external = false,
  ): boolean {
    this.clearActing();
    // Compute the candidate from the snapshot of peers we had before
    // the mutation: optimistic-persist may have already filtered out
    // the moving job, but we want the next peer that was after it in
    // the original list.
    const idx = peersBefore.findIndex((p) => p.taskKey === departingJobKey);
    let next: TaskInfo | null = null;
    if (idx >= 0) {
      next = peersBefore[idx + 1] ?? peersBefore[idx - 1] ?? null;
    } else if (peersBefore.length > 0) {
      next = peersBefore[0];
    }
    // Filter out the departing job itself (the snapshot may include
    // it; we also want to skip jobs that have since been moved out of
    // the lane).
    const live = this.jobSelection.triageLanePeers().filter(
      (p) => p.taskKey !== departingJobKey && p.state === lane,
    );
    const candidate = (next && live.find((p) => p.taskKey === next!.taskKey)) ?? live[0] ?? null;
    if (candidate) {
      // Re-anchor lane to the new job's state (same lane unless poll drift).
      this.jobSelection.triageLaneState = candidate.state;
      const token = this.jobSelection.bumpOpenDetailToken();
      this.jobSelection.syncTaskUrl(candidate, 'replace');
      // Optimistic-paint: serve a prefetched TaskDetail when available
      // so the panel re-renders without waiting for the GET roundtrip.
      // The follow-up fetch reconciles any drift on the eventual reply.
      const cached = this.prefetch.take(candidate.id, candidate.watchPath);
      if (cached) this.jobSelection.setSelectedFromAdvance(cached, token);
      this.jobService.getDetail(candidate.id, candidate.watchPath).subscribe({
        next: (detail) => this.jobSelection.setSelectedFromAdvance(detail, token),
        error: () => { /* leave panel on the previous job; the parent effect will reconcile */ },
      });
      if (external) this.jobSelection.showTriageToast('Job was moved externally; advancing.');
      return true;
    }
    if (external) {
      // External move (e.g. orchestrator decision) cleared the lane:
      // follow the job to its new state instead of closing the panel.
      // The user was looking at this task and wants to see the outcome,
      // not have the panel vanish from under them.
      const sel = this.jobSelection.selected();
      if (sel) {
        this.jobSelection.triageLaneState = sel.info.state;
        return true;
      }
    }
    // Lane cleared — close the panel and toast.
    this.jobSelection.closeDetail();
    this.jobSelection.showTriageToast('Lane cleared.');
    return false;
  }

  // ---------- external lane change (stay on current job) ----------

  /**
   * Called when an open job's lane changes via an external trigger
   * (runner auto-pickup, another client, state-machine transition).
   * Keeps the user on the current job; only shrinks the pager snapshot
   * by removing the job that just left the captured lane. The pager
   * total decrements by one; the displayed task stays put.
   */
  handleExternalLaneChange(originLane: string, taskKey: string): void {
    const snap = this.lanePager.snapshot();
    if (snap && snap.jobs.some(j => j.taskKey === taskKey)) {
      this.lanePager.dropFromSnapshot(taskKey);
    }

    const sel = this.jobSelection.selected();
    const newState = sel?.info.state;
    const newStateLabel = newState
      ? (LANE_LABELS[newState] ?? newState)
      : 'another lane';

    // Update triageLaneState to the job's new state so the shell's
    // external-move effect does not re-fire on the next tick. The pager
    // snapshot still records the *original* lane for Prev/Next navigation.
    this.jobSelection.triageLaneState = newState ?? originLane;

    const remaining = this.lanePager.snapshot()?.jobs.length ?? 0;
    if (remaining > 0) {
      this.jobSelection.showTriageToast(
        `Lane changed to ${newStateLabel}. You're still viewing this task.`,
      );
    }
  }

}
