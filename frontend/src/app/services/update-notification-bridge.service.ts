import { effect, inject, Injectable } from '@angular/core';
import { UpdateClientService } from './update.service';
import { NotificationService } from './notification.service';

/** localStorage key holding dismissed run IDs so a click-away survives F5. */
const DISMISSED_STORAGE_KEY = 'atp.update.dismissedRuns';
/** Cap on persisted dismissed run IDs; keeps the key bounded over time. */
const DISMISSED_MAX = 50;
/**
 * Freshness window for the failed toast. A failed run whose
 * `lastRunFinishedAt` is older than this is treated as stale history and
 * is NOT re-toasted on a fresh page load (where the in-memory handled /
 * dismissed sets are empty). A genuine failure during the session has a
 * recent `lastRunFinishedAt` and still surfaces immediately. The window is
 * wider than the done branch's 60 s because a failure warrants a longer
 * grace period for the operator to actually catch it after a reload.
 */
const FAILED_FRESH_WINDOW_MS = 5 * 60_000;

/**
 * F56: bridges UpdateClientService status changes to toast notifications.
 * Replaces the old <app-update-banner> component. Watches status() and
 * pushes toasts for done / done-no-change / degraded / failed states.
 *
 * Instantiated once at app bootstrap (provided in root, injected in AppComponent).
 */
@Injectable({ providedIn: 'root' })
export class UpdateNotificationBridge {
  private readonly client = inject(UpdateClientService);
  private readonly notify = inject(NotificationService);

  private activeToastId: number | null = null;
  private lastHandledRunId: string | null = null;
  private lastHandledPhase: string | null = null;
  private dismissed = this.loadDismissed();

  constructor() {
    effect(() => {
      const s = this.client.status();
      if (!s) return;
      if (s.isRunning) return;

      const runId = s.currentRunId ?? 'null-run';

      if (this.dismissed.has(runId)) return;
      // Allow upgrading a previously shown failed toast to the done toast
      // for the same runId (defense-in-depth for the cold-start retry
      // window: if the backend eventually answers, the operator should see
      // the success state, not stay parked on "Update failed").
      const sameRunIdAlreadyHandled = this.lastHandledRunId === runId;
      const isFailedToDoneUpgrade =
        sameRunIdAlreadyHandled && this.lastHandledPhase === 'failed' && s.phase === 'done';
      if (sameRunIdAlreadyHandled && !isFailedToDoneUpgrade) return;

      if (s.phase === 'failed') {
        // Freshness gate (analogous to the done branch): a long-finished
        // failed run must not re-toast on a fresh page load. Without this,
        // the update-service keeps reporting `phase: failed` for an old run
        // and — because `dismissed`/`lastHandledRunId` are in-memory and
        // empty after F5 — the toast reappears on every reload. A real
        // failure during the session has a recent `lastRunFinishedAt` and
        // is still surfaced. The failed -> done upgrade path bypasses the
        // gate (mirrors the done branch) but that path never reaches here.
        const failedFinishedMs = s.lastRunFinishedAt ? Date.parse(s.lastRunFinishedAt) : NaN;
        const failedFresh =
          !Number.isNaN(failedFinishedMs) && Date.now() - failedFinishedMs <= FAILED_FRESH_WINDOW_MS;
        if (!failedFresh) return;

        this.dismissActive();
        this.lastHandledRunId = runId;
        this.lastHandledPhase = 'failed';

        const failures = s.verificationFailures ?? [];
        const details = failures.map(
          f => `${f.step}: ${f.observed ?? '(none)'} (expected ${f.expected ?? '?'})`
        );

        // Three failure shapes, ordered most-specific first. The
        // "still starting up" substring is the contract with the backend's
        // UpdateVerifier.DescribeHttpFailure helper (see backend.Tests
        // /UpdateVerifierTests.Status0_WithBackendAliveTrue_SaysStillStartingUp).
        const hasStillStartingUp = failures.some(f => f.observed?.includes('still starting up'));
        const hasNoResponse = failures.some(f => f.observed?.includes('no response'));
        const hasVerificationFailure = failures.length > 0;
        const message = hasVerificationFailure
          ? (hasStillStartingUp
            ? 'The backend is still starting up and did not finish draining within the verification window. Wait a moment and retry; rolling back is usually not needed in this case.'
            : hasNoResponse
              ? 'The backend did not respond after the update. It may still be starting up. You can roll back to the previous version or wait and retry.'
              : `Verification failed after restart: ${failures.map(f => f.step).join(', ')} did not pass. Roll back to restore the previous version.`)
          : (s.message ?? 'The update did not succeed.');

        this.activeToastId = this.notify.notify({
          kind: 'error',
          title: 'Update failed',
          message,
          details,
          durationMs: 0,
          actions: [
            {
              label: 'Roll back',
              testId: 'toast-update-rollback',
              primary: true,
              callback: () => {
                this.recordDismissed(runId);
                this.client.rollback(runId).catch(() => { /* status poll surfaces failure */ });
              },
            },
            {
              label: 'Other runs…',
              testId: 'toast-update-other-runs',
              callback: () => {
                this.recordDismissed(runId);
                const el = document.querySelector('[data-testid="update-center-trigger"]') as HTMLElement | null;
                el?.click();
              },
            },
            {
              label: 'Dismiss',
              testId: 'toast-update-dismiss',
              callback: () => { this.recordDismissed(runId); },
            },
          ],
        });
        return;
      }

      // AGT-2862: backend up, frontend down. The run is over and nothing was
      // rolled back, so this is neither the success nor the failure toast: it
      // is a warning that names the state and offers the two moves the
      // operator actually has. Same freshness gate as the failed branch, so a
      // long-finished degraded run does not re-toast on every reload.
      if (s.phase === 'degraded') {
        const degradedFinishedMs = s.lastRunFinishedAt ? Date.parse(s.lastRunFinishedAt) : NaN;
        const degradedFresh =
          !Number.isNaN(degradedFinishedMs)
          && Date.now() - degradedFinishedMs <= FAILED_FRESH_WINDOW_MS;
        if (!degradedFresh) return;

        this.dismissActive();
        this.lastHandledRunId = runId;
        this.lastHandledPhase = 'degraded';

        const degradedFailures = s.verificationFailures ?? [];
        this.activeToastId = this.notify.notify({
          kind: 'warning',
          title: 'Update finished with the frontend down',
          message:
            s.message
            ?? 'The backend is up on the new release; the frontend dev server did not answer. The checkout was left on the new version.',
          details: degradedFailures.map(
            f => `${f.step}: ${f.observed ?? '(none)'} (expected ${f.expected ?? '?'})`
          ),
          durationMs: 0,
          actions: [
            {
              label: 'Roll back',
              testId: 'toast-update-rollback',
              callback: () => {
                this.recordDismissed(runId);
                this.client.rollback(runId).catch(() => { /* status poll surfaces failure */ });
              },
            },
            {
              label: 'Dismiss',
              testId: 'toast-update-dismiss',
              callback: () => { this.recordDismissed(runId); },
            },
          ],
        });
        return;
      }

      if (s.phase === 'done' && s.lastRunFinishedAt) {
        const finishedMs = Date.parse(s.lastRunFinishedAt);
        // The 60 s freshness gate is bypassed for the failed -> done
        // upgrade path: if the operator already saw a "failed" toast, the
        // belated success must overwrite it even when several minutes
        // have passed (e.g. cold-start drain that finally returned 200).
        const fresh = !Number.isNaN(finishedMs) && Date.now() - finishedMs <= 60_000;
        if (!fresh && !isFailedToDoneUpgrade) return;

        this.dismissActive();
        this.lastHandledRunId = runId;
        this.lastHandledPhase = 'done';

        const hasCodeChange = s.lastRunHeadAfter && s.lastRunHeadBefore && s.lastRunHeadAfter !== s.lastRunHeadBefore;

        if (hasCodeChange) {
          this.activeToastId = this.notify.notify({
            kind: 'success',
            title: 'Update finished',
            message: `${s.lastRunHeadBefore?.slice(0, 7)} → ${s.lastRunHeadAfter?.slice(0, 7)}. Reload required for the FE to pick up new code.`,
            durationMs: 0,
            actions: [
              {
                label: 'Reload',
                testId: 'toast-update-reload',
                primary: true,
                callback: () => {
                  this.recordDismissed(runId);
                  if (typeof window !== 'undefined') window.location.reload();
                },
              },
              {
                label: 'Dismiss',
                testId: 'toast-update-done-dismiss',
                callback: () => { this.recordDismissed(runId); },
              },
            ],
          });
        } else {
          this.activeToastId = this.notify.notify({
            kind: 'info',
            title: 'Update finished',
            message: `No code change (${s.lastRunHeadAfter?.slice(0, 7) ?? '?'}); reload not required.`,
            durationMs: 6000,
          });
          this.recordDismissed(runId);
        }
      }
    });
  }

  private dismissActive(): void {
    if (this.activeToastId !== null) {
      this.notify.dismiss(this.activeToastId);
      this.activeToastId = null;
    }
  }

  /** Mark a run dismissed for this session AND persist it across reloads. */
  private recordDismissed(runId: string): void {
    this.dismissed.add(runId);
    this.persistDismissed();
  }

  private loadDismissed(): Set<string> {
    try {
      if (typeof localStorage === 'undefined') return new Set();
      const raw = localStorage.getItem(DISMISSED_STORAGE_KEY);
      if (!raw) return new Set();
      const parsed: unknown = JSON.parse(raw);
      if (!Array.isArray(parsed)) return new Set();
      return new Set(parsed.filter((x): x is string => typeof x === 'string'));
    } catch {
      // Corrupt / disabled storage must never break the toast bridge.
      return new Set();
    }
  }

  private persistDismissed(): void {
    try {
      if (typeof localStorage === 'undefined') return;
      // Keep only the most-recent IDs so the key cannot grow unbounded.
      const ids = [...this.dismissed].slice(-DISMISSED_MAX);
      localStorage.setItem(DISMISSED_STORAGE_KEY, JSON.stringify(ids));
    } catch {
      // Storage full / disabled — the in-memory set still gates this session.
    }
  }
}
