import { Injectable, inject } from '@angular/core';
import { Observable, from, switchMap } from 'rxjs';
import { NotificationService } from './notification.service';
import { TaskService } from './task.service';
import { ErrorDialogService } from './error-dialog.service';

/**
 * One-slot undo controller for state-changing actions triggered from the
 * task-detail header (top-right): Complete, Mark as Done, any
 * overflow-menu lane move, lane dropdown, Move-to-top.
 *
 * Contract:
 *  - Callers capture whatever they need to reverse the action BEFORE
 *    firing the mutation, then call `offer(...)` as soon as the optimistic
 *    paint lands. The supplied `revert` returns the HTTP observable for
 *    the reverse API call; UndoController subscribes and paints the
 *    "Restored" toast on success.
 *  - One pending undo at a time. Offering a new one supersedes the
 *    previous toast.
 *  - Auto-dismiss after `WINDOW_MS` (8s). Click "Undo" within the window
 *    to issue the reverse API call.
 *  - The revert itself does NOT spawn another undo toast; a short
 *    "Restored" success toast confirms the outcome.
 *
 * Delete is not covered yet: the backend hard-deletes the folder, so
 * undo requires a workspace-level `.trash/` buffer + restore endpoint
 * that does not exist today. Tracked as a follow-up; the task prompt
 * explicitly allows deferring delete-undo as long as Complete and
 * lane-moves are covered (see prompt "Delete undo" section).
 */
@Injectable({ providedIn: 'root' })
export class UndoController {
  private readonly notifications = inject(NotificationService);
  private readonly jobService = inject(TaskService);
  private readonly errorDialog = inject(ErrorDialogService);

  private static readonly WINDOW_MS = 8000;

  private activeToastId: number | null = null;
  private activeTimer: ReturnType<typeof setTimeout> | null = null;

  /**
   * Offer an undo toast. The toast message reads
   * `"{actionLabel} \"{jobLabel}\" → {targetLaneLabel}   [Undo]"`. The
   * undo button invokes `revert()` to issue the reverse API call.
   *
   * `revert` returns an Observable so we can also use it for non-HTTP
   * reverts in the future without locking the signature down. Failures
   * surface via the standard error dialog.
   */
  offer(params: {
    jobId: string;
    jobLabel: string;
    actionLabel: string;
    targetLaneLabel: string;
    revert: () => Observable<unknown>;
  }): void {
    this.cancelActive();

    const { jobId, jobLabel, actionLabel, targetLaneLabel, revert } = params;
    const message = `${actionLabel} "${jobLabel}" → ${targetLaneLabel}`;

    // The NotificationService forces durationMs=0 whenever a toast
    // carries action buttons (so a long-lived "Reload required" / error
    // dialog never vanishes mid-read). The undo toast deliberately
    // wants the opposite: short auto-dismiss window so a fresh
    // mutation toast supersedes it without the operator drowning in
    // stacked toasts. We therefore drive the timeout ourselves rather
    // than changing the service-level contract.
    const id = this.notifications.notify({
      kind: 'info',
      message,
      // Dock bottom-right so the Move/Undo toast never covers the
      // task-detail context menu, which opens in the same top-right
      // corner as the default notification pile.
      position: 'bottom-right',
      actions: [
        {
          label: 'Undo',
          primary: true,
          testId: 'undo-action',
          callback: () => this.runRevert(jobId, jobLabel, revert),
        },
      ],
    });

    this.activeToastId = id;
    this.activeTimer = setTimeout(() => {
      if (this.activeToastId === id) {
        this.notifications.dismiss(id);
        this.activeToastId = null;
        this.activeTimer = null;
      }
    }, UndoController.WINDOW_MS);
  }

  /**
   * Convenience for the common case: revert a cross-lane move by
   * calling `moveJob(prevState, watchPath, prevIndex)`. The backend's
   * MoveAsync handler applies SetOrderInLane after the cross-state move
   * so the card lands back at the same slot.
   */
  offerLaneRevert(params: {
    jobId: string;
    watchPath: string;
    jobLabel: string;
    actionLabel: string;
    targetLaneLabel: string;
    prevState: string;
    prevIndex: number;
    persisted?: Promise<void>;
  }): void {
    this.offer({
      jobId: params.jobId,
      jobLabel: params.jobLabel,
      actionLabel: params.actionLabel,
      targetLaneLabel: params.targetLaneLabel,
      revert: () => {
        // Optimistically paint the revert so the card visibly returns
        // to its origin lane immediately; the backend `moveJob` then
        // catches up. Order-restoration is handled server-side via
        // SetOrderInLane (see JobTransitionService.MoveAsync).
        const snapshot = this.jobService.applyOptimisticMove(
          params.jobId,
          params.watchPath,
          params.prevState,
          params.prevIndex,
        );
        this.jobService.beginOptimisticPersist();
        return from(params.persisted ?? Promise.resolve()).pipe(
          switchMap(() => new Observable<unknown>((subscriber) => {
            this.jobService.moveJob(params.jobId, params.prevState, params.watchPath, params.prevIndex)
            .subscribe({
              next: (v) => {
                this.jobService.endOptimisticPersist();
                this.jobService.refresh(true);
                subscriber.next(v);
                subscriber.complete();
              },
              error: (err) => {
                this.jobService.endOptimisticPersist();
                if (snapshot) this.jobService.revertOptimisticMove(snapshot);
                subscriber.error(err);
              },
            });
          })),
        );
      },
    });
  }

  /**
   * Convenience for same-lane reorders (e.g. Move-to-top). The caller
   * captures the lane's ordered job list BEFORE the action; the undo
   * replays that order via `/api/tasks/reorder` so the card returns to
   * its original slot. `paintOrder` is the whole lane to repaint when
   * `prevOrder` covers only part of it (one project's queue in a mixed lane).
   */
  offerReorderRevert(params: {
    jobId: string;
    jobLabel: string;
    actionLabel: string;
    targetLaneLabel: string;
    prevOrder: { jobId: string; watchPath: string }[];
    laneState: string;
    paintOrder?: { jobId: string; watchPath: string }[];
  }): void {
    this.offer({
      jobId: params.jobId,
      jobLabel: params.jobLabel,
      actionLabel: params.actionLabel,
      targetLaneLabel: params.targetLaneLabel,
      revert: () => {
        const before = this.jobService.applyOptimisticReorder(
          params.laneState, params.paintOrder ?? params.prevOrder);
        this.jobService.beginOptimisticPersist();
        return new Observable<unknown>((subscriber) => {
          this.jobService.reorderJobs(params.prevOrder).subscribe({
            next: (v) => {
              this.jobService.endOptimisticPersist();
              this.jobService.refresh(true);
              subscriber.next(v);
              subscriber.complete();
            },
            error: (err) => {
              this.jobService.endOptimisticPersist();
              if (before) this.jobService.revertOptimisticReorder(params.laneState, before);
              subscriber.error(err);
            },
          });
        });
      },
    });
  }

  private runRevert(jobId: string, jobLabel: string, revert: () => Observable<unknown>): void {
    this.activeToastId = null;
    if (this.activeTimer !== null) {
      clearTimeout(this.activeTimer);
      this.activeTimer = null;
    }
    revert().subscribe({
      next: () => {
        // Plain success toast: no second undo, no infinite loop.
        this.notifications.success(`Restored "${jobLabel}"`);
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Undo failed',
          fallbackMessage: 'Could not restore the task to its previous lane.',
          source: `Task ${jobId}`,
        });
      },
    });
  }

  /** Drop the active undo toast (e.g. when a fresh undoable action takes its place). */
  cancelActive(): void {
    if (this.activeTimer !== null) {
      clearTimeout(this.activeTimer);
      this.activeTimer = null;
    }
    if (this.activeToastId !== null) {
      this.notifications.dismiss(this.activeToastId);
      this.activeToastId = null;
    }
  }
}
