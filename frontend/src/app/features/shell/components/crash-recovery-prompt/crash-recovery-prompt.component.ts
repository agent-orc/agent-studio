import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, effect, inject, signal } from '@angular/core';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import type { CrashRecoveryPending } from '../../../../models/task.model';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';

@Component({
  selector: 'app-crash-recovery-prompt',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, DialogComponent, StudioIconComponent],
  templateUrl: './crash-recovery-prompt.component.html',
  styleUrl: './crash-recovery-prompt.component.scss',
})
export class CrashRecoveryPromptComponent implements OnInit {
  private readonly tasks = inject(TaskService);
  private readonly modalStack = inject(ModalStackService);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);

  readonly pending = signal<CrashRecoveryPending[]>([]);
  readonly reviewPending = computed(() =>
    this.pending().filter(item => item.classification !== 'trivial'));
  readonly trivialPending = computed(() =>
    this.pending().filter(item => item.classification === 'trivial'));
  readonly loading = signal(false);
  readonly busyId = signal<string | null>(null);
  readonly busyAll = signal(false);
  readonly error = signal<string | null>(null);
  /** Only explicit operator opening may cover the current view. */
  readonly open = signal(false);

  private stackDispose: (() => void) | null = null;
  private trivialNotificationId: number | null = null;
  private trivialNotificationFingerprint = '';
  /// Set while a bulk dismiss skips stale (404) entries; triggers one
  /// authoritative refresh after the queue drains.
  private staleNoticed = false;

  constructor() {
    effect(() => {
      if (this.open()) {
        if (!this.stackDispose) {
          this.stackDispose = this.modalStack.push('crash-recovery-prompt', () => {
            this.closeLocal();
            return true;
          });
        }
      } else if (this.stackDispose) {
        this.stackDispose();
        this.stackDispose = null;
      }
    });
    this.destroyRef.onDestroy(() => {
      this.stackDispose?.();
      if (this.trivialNotificationId !== null) {
        this.notifications.dismiss(this.trivialNotificationId);
      }
    });
  }

  ngOnInit(): void {
    this.refresh();
  }

  openRecovery(): void {
    if (this.reviewPending().length === 0) return;
    this.open.set(true);
    this.refresh();
  }

  /** Closing this view does not acknowledge the shared recovery decision. */
  closeLocal(): void {
    this.open.set(false);
    this.error.set(null);
  }

  refresh(): void {
    this.loading.set(true);
    this.error.set(null);
    this.tasks.getPendingCrashRecoveries().subscribe({
      next: (res) => {
        this.pending.set(res.pending ?? []);
        if (this.reviewPending().length === 0) this.closeLocal();
        this.syncTrivialNotification();
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(this.errorMessage(err, 'Could not load pending crash recovery items.'));
        this.loading.set(false);
      },
    });
  }

  commit(item: CrashRecoveryPending): void {
    if (this.busyId() || this.busyAll()) return;
    this.busyId.set(item.id);
    this.error.set(null);
    this.tasks.commitCrashRecovery(item.id).subscribe({
      next: () => {
        this.busyId.set(null);
        this.remove(item.id);
      },
      error: (err) => {
        this.busyId.set(null);
        if (this.isStaleItem(err)) {
          this.healStaleItem(item);
          return;
        }
        this.error.set(this.errorMessage(err, 'Could not commit crash recovery changes.'));
      },
    });
  }

  /// Dismisses every pending item in sequence. Already-dismissed items stay
  /// removed when a later one fails; the error then names the failing item
  /// and the rest remain reviewable.
  dismissAll(): void {
    if (this.busyId() || this.busyAll()) return;
    this.busyAll.set(true);
    this.error.set(null);
    const queue = [...this.reviewPending()];
    const next = () => {
      const item = queue.shift();
      if (!item) {
        this.busyAll.set(false);
        if (this.staleNoticed) {
          this.staleNoticed = false;
          this.notifications.notify({
            kind: 'info',
            title: 'Crash recovery list was outdated',
            message: 'Some entries were already resolved elsewhere. The list has been reloaded.',
            durationMs: 6000,
          });
          this.refresh();
        }
        return;
      }
      this.tasks.dismissCrashRecovery(item.id).subscribe({
        next: () => {
          this.remove(item.id);
          next();
        },
        error: (err) => {
          // A stale entry (already resolved elsewhere, e.g. after a backend
          // restart renumbered the pending list) must not abort the queue:
          // drop it locally and keep dismissing the rest.
          if (this.isStaleItem(err)) {
            this.remove(item.id);
            this.staleNoticed = true;
            next();
            return;
          }
          this.busyAll.set(false);
          this.error.set(this.errorMessage(err, `Could not dismiss '${item.projectName}'.`));
        },
      });
    };
    next();
  }

  dismiss(item: CrashRecoveryPending): void {
    if (this.busyId() || this.busyAll()) return;
    this.busyId.set(item.id);
    this.error.set(null);
    this.tasks.dismissCrashRecovery(item.id).subscribe({
      next: () => {
        this.busyId.set(null);
        this.remove(item.id);
      },
      error: (err) => {
        this.busyId.set(null);
        if (this.isStaleItem(err)) {
          this.healStaleItem(item);
          return;
        }
        this.error.set(this.errorMessage(err, 'Could not dismiss crash recovery item.'));
      },
    });
  }

  fileCountLabel(item: CrashRecoveryPending): string {
    const count = item.files?.length ?? 0;
    return `${count} changed file${count === 1 ? '' : 's'}`;
  }

  private remove(id: string): void {
    this.pending.update(items => items.filter(item => item.id !== id));
    if (this.reviewPending().length === 0) this.closeLocal();
    this.syncTrivialNotification();
  }

  private syncTrivialNotification(): void {
    const items = this.trivialPending();
    const fingerprint = items
      .map(item => `${item.id}:${item.files.join('|')}`)
      .sort()
      .join(';');
    const notificationStillVisible = this.trivialNotificationId !== null
      && this.notifications.notifications().some(item => item.id === this.trivialNotificationId);

    if (items.length === 0) {
      if (this.trivialNotificationId !== null) {
        this.notifications.dismiss(this.trivialNotificationId);
      }
      this.trivialNotificationId = null;
      this.trivialNotificationFingerprint = '';
      return;
    }
    if (notificationStillVisible && fingerprint === this.trivialNotificationFingerprint) return;
    if (notificationStillVisible && this.trivialNotificationId !== null) {
      this.notifications.dismiss(this.trivialNotificationId);
    }

    const fileCount = items.reduce((total, item) => total + item.files.length, 0);
    const details = items.map(item =>
      `${item.projectName}: ${this.fileCountLabel(item)}`);
    this.trivialNotificationFingerprint = fingerprint;
    this.trivialNotificationId = this.notifications.notify({
      kind: 'info',
      title: 'Crash recovery found read-evidence sidecars',
      message: `${fileCount} metadata sidecar ${fileCount === 1 ? 'file remains' : 'files remain'} uncommitted. The board is ready to use.`,
      source: 'No task attribution',
      details,
      durationMs: 0,
      actions: [{
        label: 'Leave uncommitted',
        testId: 'crash-recovery-trivial-dismiss',
        primary: true,
        callback: () => this.dismissAllTrivial(),
      }],
    });
  }

  private dismissAllTrivial(): void {
    if (this.busyAll()) return;
    this.busyAll.set(true);
    const queue = [...this.trivialPending()];
    const next = () => {
      const item = queue.shift();
      if (!item) {
        this.busyAll.set(false);
        return;
      }
      this.tasks.dismissCrashRecovery(item.id).subscribe({
        next: () => {
          this.remove(item.id);
          next();
        },
        error: (err) => {
          if (this.isStaleItem(err)) {
            this.remove(item.id);
            next();
            return;
          }
          this.busyAll.set(false);
          this.trivialNotificationId = null;
          this.syncTrivialNotification();
          this.notifications.error(
            this.errorMessage(err, `Could not leave '${item.projectName}' uncommitted.`),
            'Crash recovery action failed',
          );
        },
      });
    };
    next();
  }

  /// True when the backend answered 404: the entry no longer exists server-side
  /// (resolved elsewhere, or the backend restarted and renumbered the list).
  private isStaleItem(err: unknown): boolean {
    return (err as { status?: number })?.status === 404;
  }

  /// Self-heal for a stale entry outside the bulk queues: drop it locally,
  /// re-fetch the authoritative list, and tell the user what happened instead
  /// of leaving a dead error banner over an outdated list.
  private healStaleItem(item: CrashRecoveryPending): void {
    this.remove(item.id);
    this.notifications.notify({
      kind: 'info',
      title: 'Crash recovery list was outdated',
      message: `'${item.projectName}' was already resolved elsewhere. The list has been reloaded.`,
      durationMs: 6000,
    });
    this.refresh();
  }

  private errorMessage(err: unknown, fallback: string): string {
    const anyErr = err as { error?: { error?: string }; message?: string };
    return anyErr?.error?.error || anyErr?.message || fallback;
  }
}
