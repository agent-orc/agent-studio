import { Injectable, signal } from '@angular/core';
import {
  NotificationKind,
  NotificationOptions,
  NotificationState,
} from '../models/app-dialog.model';

const DEFAULT_DURATION: Record<NotificationKind, number> = {
  success: 5000,
  info: 5000,
  warning: 0,
  error: 0,
  accent: 0,
};

/**
 * Stack-based notification surface. Renders styled toasts (success / info /
 * warning / error) via `<app-notification-stack>` mounted at the app shell.
 *
 * Use for non-blocking outcomes ("Lane cleared", "Task deleted", "Settings
 * saved"). For full error overlays with stack trace + copy, keep using
 * `ErrorDialogService.show` — both surfaces share the same visual language.
 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private renderPendingFeedback: (() => void) | null = null;
  readonly notifications = signal<NotificationState[]>([]);

  private nextId = 1;
  private timers = new Map<number, ReturnType<typeof setTimeout>>();

  constructor() {
    // Expose the live instance on window so E2E screenshot specs can
    // drive every kind side-by-side without coupling to a feature flow.
    // No-op outside the browser; harmless in production builds.
    if (typeof window !== 'undefined') {
      (window as unknown as { __notifications?: NotificationService }).__notifications = this;
    }
  }

  notify(options: NotificationOptions): number {
    const id = this.nextId++;
    const hasActions = options.actions && options.actions.length > 0;
    const durationMs = hasActions ? 0 : (options.durationMs ?? DEFAULT_DURATION[options.kind]);
    const state: NotificationState = { id, durationMs, ...options };

    this.notifications.update((arr) => [...arr, state]);
    // A notification often accompanies a deliberately still-pending HTTP
    // request. Zone-based change detection otherwise waits for that request
    // to settle before painting the toast, defeating optimistic feedback.
    queueMicrotask(() => this.renderPendingFeedback?.());
    if (durationMs > 0) {
      const timer = setTimeout(() => this.dismiss(id), durationMs);
      this.timers.set(id, timer);
    }
    return id;
  }

  /** Update a live toast in place without changing its dismissal timer. */
  update(id: number, patch: Partial<NotificationOptions>): boolean {
    let found = false;
    this.notifications.update((arr) => arr.map((notification) => {
      if (notification.id !== id) return notification;
      found = true;
      return { ...notification, ...patch, id };
    }));
    if (found) queueMicrotask(() => this.renderPendingFeedback?.());
    return found;
  }

  dismissTopmost(): void {
    const all = this.notifications();
    if (all.length > 0) this.dismiss(all[0].id);
  }

  /**
   * AGT-3830: dismisses the newest notice that stays until closed and returns
   * true, or returns false when none is open. A surface that owns Escape (the
   * task detail) calls this first, so Escape closes a sticky notice before it
   * closes the surface under it.
   */
  dismissNewestPersistent(): boolean {
    const sticky = this.notifications().filter((n) => !n.durationMs);
    const newest = sticky[sticky.length - 1];
    if (!newest) return false;
    this.dismiss(newest.id);
    return true;
  }

  success(message: string, title?: string): number {
    return this.notify({ message, title, kind: 'success' });
  }

  info(message: string, title?: string): number {
    return this.notify({ message, title, kind: 'info' });
  }

  warning(message: string, title?: string): number {
    return this.notify({ message, title, kind: 'warning' });
  }

  error(message: string, title?: string): number {
    return this.notify({ message, title, kind: 'error' });
  }

  dismiss(id: number): void {
    const timer = this.timers.get(id);
    if (timer) {
      clearTimeout(timer);
      this.timers.delete(id);
    }
    this.notifications.update((arr) => arr.filter((n) => n.id !== id));
  }

  dismissAll(): void {
    for (const timer of this.timers.values()) clearTimeout(timer);
    this.timers.clear();
    this.notifications.set([]);
  }

  /** Register the single stack renderer so pending-request toasts paint now. */
  registerRenderer(render: () => void): () => void {
    this.renderPendingFeedback = render;
    return () => {
      if (this.renderPendingFeedback === render) this.renderPendingFeedback = null;
    };
  }
}
