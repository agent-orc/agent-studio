/**
 * Shared models for the unified app dialog family:
 *  - confirm dialogs (delete / discard / load-all-style guard rails)
 *  - notification popups (success / info / warning / error)
 *
 * The full error overlay with stack trace + copy lives in
 * `error-dialog.model.ts`; this file covers the lighter-weight surfaces
 * that previously fell back to `window.confirm` and ad-hoc toasts.
 */

export type NotificationKind = 'success' | 'info' | 'warning' | 'error' | 'accent';

/**
 * Where the toast docks on screen. Defaults to `top-right` (the shared
 * notification pile). `bottom-right` is reserved for toasts that would
 * otherwise collide with a top-right surface — e.g. the Move/Undo action
 * toast, which must not cover the task-detail context menu that opens in
 * the same top-right corner.
 */
export type NotificationPosition = 'top-right' | 'bottom-right';

export type ConfirmDialogKind = 'danger' | 'primary';

export interface NotificationAction {
  label: string;
  testId?: string;
  primary?: boolean;
  callback: () => void;
}

export interface ConfirmDialogOptions {
  title: string;
  message: string;
  /** Extra body line below the message, e.g. the affected item's name. */
  detail?: string;
  /** Every item the action touches, one per row (AGT-3585 bulk actions list the cards). */
  items?: string[];
  confirmLabel?: string;
  cancelLabel?: string;
  /** Visual flavour. `danger` is the default for delete-style flows. */
  kind?: ConfirmDialogKind;
  /**
   * Type-to-confirm gate. When non-empty, the dialog renders a text input
   * and keeps the confirm button disabled until the trimmed input matches
   * one of these values (case-insensitive). Used by the destructive
   * project-delete flow so the operator must re-type the project name or
   * its short code before the second confirmation can complete.
   */
  requireTypedValues?: string[];
  /** Label shown above the type-to-confirm input. */
  requireTypedPrompt?: string;
  /**
   * Written-reason gate (AGT-3624). When set, the dialog renders a text area
   * and keeps the confirm button disabled until the trimmed reason has at
   * least `minLength` characters. Use `ConfirmDialogService.confirmWithReason`
   * to receive the reason. Archive overrides set the same bar.
   */
  requireReason?: ConfirmReasonGate;
}

export interface ConfirmReasonGate {
  /** Label shown above the reason input. */
  prompt: string;
  minLength: number;
}

export interface ConfirmDialogState
  extends Required<Omit<ConfirmDialogOptions, 'detail' | 'items' | 'requireTypedValues' | 'requireTypedPrompt' | 'requireReason'>> {
  detail: string | null;
  items: string[] | null;
  requireTypedValues: string[] | null;
  requireTypedPrompt: string | null;
  requireReason: ConfirmReasonGate | null;
}

export interface NotificationOptions {
  message: string;
  title?: string;
  kind: NotificationKind;
  /**
   * Auto-dismiss after this many ms. Pass `0` to keep it open until the
   * user dismisses or the caller calls `dismiss(id)`. When omitted the
   * service picks a sensible default per `kind` (errors stay longer).
   */
  durationMs?: number;
  /**
   * Optional small caption rendered as its own sub-line under the
   * message (typically the project / job slug that produced the
   * notification). Callers must put it here instead of concatenating
   * "... in <project>" onto `message` so the toast layout can clamp the
   * body independently of the source label.
   */
  source?: string;
  /** Optional detail lines rendered below the message (e.g. verification failures). */
  details?: string[];
  /** Action buttons rendered in the toast footer. */
  actions?: NotificationAction[];
  /**
   * Where the toast docks. Omit for the default `top-right` pile; set
   * `bottom-right` to keep a toast clear of a top-right surface it would
   * otherwise occlude (see {@link NotificationPosition}).
   */
  position?: NotificationPosition;
}

export interface NotificationState extends NotificationOptions {
  id: number;
  durationMs: number;
}
