import { Injectable, inject } from '@angular/core';
import type { TaskInfo } from '../../../models/task.model';
import type { NotificationAction } from '../../../models/app-dialog.model';
import { TaskService } from '../../../services/task.service';
import { NotificationService } from '../../../services/notification.service';
import { serverErrorMessage } from '../../../utils/server-error-message';
import { AcceptanceReadinessStore } from '../../acceptance-readiness';
import { StudioTabStateService } from '../../studio-shell/services/studio-tab-state.service';
import { TaskSelectionService } from './task-selection.service';
import {
  AcceptanceRefusalStore,
  acceptanceRefusalConcern,
  acceptanceRefusalOverridable,
  serverAcceptanceRefusal,
  type ServerAcceptanceRefusal,
} from './acceptance-refusal.model';

/** Toast wording of the override action; the toolbar form behind it is `AcceptanceRefusalComponent`. */
export const OVERRIDE_ACTION_LABEL = 'Accept with a written reason';
export const REVIEW_AGAIN_LABEL = 'Review again';

/**
 * AGT-3873: the steps that resolve a refused Accept, shared by the refusal
 * toast, the card banner and a refused Accept on the board. The owner pressed
 * Accept on AGT-3534, the server refused it for a `concerns` verdict, and the
 * toast and the banner named the override without offering it while the toast
 * covered the toolbar action that did.
 *
 * The override is always the toolbar's written-reason form
 * (`AcceptanceRefusalComponent.acceptWithOverride`): this service only opens
 * the card when another one shows and asks that form to open, focused.
 */
@Injectable({ providedIn: 'root' })
export class AcceptanceRefusalActions {
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  private readonly refusals = inject(AcceptanceRefusalStore);
  private readonly readiness = inject(AcceptanceReadinessStore);
  private readonly selection = inject(TaskSelectionService);
  private readonly studioTabs = inject(StudioTabStateService);

  /** Opens `info` unless it already shows, then its written-reason override form, focused. */
  openOverride(info: TaskInfo): void {
    if (this.selection.selected()?.info.taskKey !== info.taskKey) {
      this.studioTabs.open({ kind: 'task', taskKey: info.taskKey });
      this.selection.openDetail(info);
    }
    this.refusals.requestOverride(info.taskKey);
  }

  /** Requests the content review of `info` against its current brief. */
  reviewAgain(info: TaskInfo): void {
    this.tasks.reviewContentAgainstCurrentBrief(info.id, info.watchPath).subscribe({
      next: () => {
        this.refusals.clear(info.taskKey);
        this.notifications.success('Content review requested for the current brief.');
        this.tasks.refresh(true);
      },
      error: (err: unknown) => this.notifications.error(
        serverErrorMessage(err, 'Could not request content review.'), 'Review again failed'),
    });
  }

  /**
   * Keeps the refusal next to Accept and toasts it below the toolbar with the
   * actions that resolve it, or with what to do instead when no override can
   * admit the card.
   */
  report(info: TaskInfo, refusal: ServerAcceptanceRefusal): void {
    this.refusals.record(refusal);
    const content = refusal.code === 'completion-content-gap';
    const concern = content ? acceptanceRefusalConcern(info, this.readiness.itemFor(info)) : null;
    const overridable = acceptanceRefusalOverridable(refusal);
    const actions: NotificationAction[] = overridable
      ? [
          { label: OVERRIDE_ACTION_LABEL, primary: true, testId: 'acceptance-refusal-toast-override',
            callback: () => this.openOverride(info) },
          { label: REVIEW_AGAIN_LABEL, testId: 'acceptance-refusal-toast-review',
            callback: () => this.reviewAgain(info) },
        ]
      : [];
    const details = [
      ...(concern ? [`The review's concern: ${concern}`] : []),
      ...(content && !overridable
        ? ['Send the round that closes the gap from Acceptance next steps on the card; an override cannot replace the missing report.']
        : []),
    ];
    this.notifications.notify({
      kind: 'warning', title: 'Accept refused', message: refusal.message,
      details: details.length > 0 ? details : undefined, actions, position: 'below-toolbar',
    });
    this.tasks.refresh(true);
  }

  /**
   * A board Accept (card menu or drag) the server refused. An overridable
   * content refusal opens the card with the written-reason form focused and
   * returns true; any other refusal returns false so the caller says what to
   * do instead.
   */
  acceptRefusedOnBoard(info: TaskInfo, err: unknown): boolean {
    if ((err as { status?: number } | null)?.status !== 409) return false;
    const refusal = serverAcceptanceRefusal(info, err);
    if (!acceptanceRefusalOverridable(refusal)) return false;
    this.report(info, refusal);
    void this.readiness.checkCard(info);
    this.openOverride(info);
    return true;
  }
}
