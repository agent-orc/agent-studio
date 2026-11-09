import { Injectable, signal } from '@angular/core';
import {
  ConfirmDialogKind,
  ConfirmDialogOptions,
  ConfirmDialogState,
} from '../models/app-dialog.model';

/**
 * App-wide confirm dialog. Replaces `window.confirm` so every confirmation
 * shares the Catppuccin-inspired look, focus trap, Esc/Enter handling, and
 * unified button styling defined in the `app-confirm-dialog` component.
 *
 * Usage:
 *   const ok = await confirmDialog.confirm({
 *     title: 'Delete this task?',
 *     message: 'This removes the job folder and all its files.',
 *     detail: `"${job.title}"`,
 *     confirmLabel: 'Delete',
 *     kind: 'danger',
 *   });
 *   if (!ok) return;
 *
 * Only one confirm is open at a time; opening a new one while another is
 * pending auto-rejects the previous prompt (the caller's await resolves
 * with `false`).
 */
@Injectable({ providedIn: 'root' })
export class ConfirmDialogService {
  readonly active = signal<ConfirmDialogState | null>(null);

  private resolver: ((value: boolean) => void) | null = null;
  private acceptedReason: string | null = null;

  constructor() {
    // Expose the live instance on window so E2E screenshot specs can
    // surface every variant (danger / primary) without driving a
    // feature flow. No-op outside the browser.
    if (typeof window !== 'undefined') {
      (window as unknown as { __confirmDialog?: ConfirmDialogService }).__confirmDialog = this;
    }
  }

  confirm(options: ConfirmDialogOptions): Promise<boolean> {
    if (this.resolver) {
      const previous = this.resolver;
      this.resolver = null;
      previous(false);
    }

    const state: ConfirmDialogState = {
      title: options.title,
      message: options.message,
      detail: options.detail ?? null,
      items: options.items?.length ? options.items : null,
      confirmLabel: options.confirmLabel ?? 'Confirm',
      cancelLabel: options.cancelLabel ?? 'Cancel',
      kind: options.kind ?? 'danger',
      requireTypedValues: options.requireTypedValues?.length ? options.requireTypedValues : null,
      requireTypedPrompt: options.requireTypedPrompt ?? null,
      requireReason: options.requireReason ?? null,
    };
    this.active.set(state);

    return new Promise<boolean>((resolve) => {
      this.resolver = resolve;
    });
  }

  /**
   * Confirm with a written-reason gate. Resolves with the trimmed reason, or
   * null when the operator cancels.
   */
  async confirmWithReason(
    options: ConfirmDialogOptions & { requireReason: NonNullable<ConfirmDialogOptions['requireReason']> },
  ): Promise<string | null> {
    this.acceptedReason = null;
    const ok = await this.confirm(options);
    const reason = this.acceptedReason;
    this.acceptedReason = null;
    return ok ? reason : null;
  }

  accept(reason?: string): void {
    this.acceptedReason = reason?.trim() || null;
    this.settle(true);
  }

  cancel(): void {
    this.settle(false);
  }

  private settle(value: boolean): void {
    const resolver = this.resolver;
    this.resolver = null;
    this.active.set(null);
    if (resolver) resolver(value);
  }
}

/** Public constants so call sites can express intent without magic strings. */
export const CONFIRM_KIND_DANGER: ConfirmDialogKind = 'danger';
export const CONFIRM_KIND_PRIMARY: ConfirmDialogKind = 'primary';
