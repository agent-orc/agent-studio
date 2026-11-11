import type { TaskInfo } from '../models/task.model';
import { unintegratedDeliveryLoss } from '../models/lane-move-guard';
import type { ConfirmDialogService } from './confirm-dialog.service';

/**
 * Minimum written-reason length. Mirrors backend
 * `CompletionContractPolicy.MinimumOverrideReasonLength`, the bar an archive
 * override has to meet.
 */
export const DELETE_REASON_MIN_LENGTH = 8;

/** A confirmed delete; `reason` is set when unintegrated work is lost. */
export interface TaskDeleteConfirmation {
  reason: string | null;
}

/**
 * AGT-3624: the one delete confirmation for board and detail. A card whose
 * delivery is not integrated names what is lost and needs a written reason,
 * the same strength as an archive override. Resolves null on cancel.
 */
export async function confirmTaskDelete(
  dialog: ConfirmDialogService,
  job: TaskInfo,
): Promise<TaskDeleteConfirmation | null> {
  const label = job.title || job.id;
  const loss = unintegratedDeliveryLoss(job);
  if (!loss) {
    const ok = await dialog.confirm({
      title: 'Delete this task?',
      message: 'This removes the job folder and all its files (prompt, logs, results). Do you really want this?',
      detail: label,
      confirmLabel: 'Delete',
      cancelLabel: 'Keep',
      kind: 'danger',
    });
    return ok ? { reason: null } : null;
  }
  const reason = await dialog.confirmWithReason({
    title: 'Delete this task and its unintegrated work?',
    message: loss,
    detail: label,
    confirmLabel: 'Delete task and work',
    cancelLabel: 'Keep',
    kind: 'danger',
    requireReason: {
      prompt: 'Why is this work no longer needed? The reason is recorded with the deletion.',
      minLength: DELETE_REASON_MIN_LENGTH,
    },
  });
  return reason ? { reason } : null;
}
