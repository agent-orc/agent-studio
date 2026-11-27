import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PendingButtonDirective } from '@studio/ui/components/async-feedback/pending-button.directive';
import type { HostReleaseDriftSnapshot } from '../../models/host-release-drift';
import { reviewHeartbeatProvesUpdate, reviewUpdateStepFromStatus,
  type ReviewDrainStatus, type ReviewUpdateTarget } from '../../models/review-host-update';

type Stage = 'checking' | 'unavailable' | 'ready' | 'draining' | 'blocked' | 'quiet' | 'switching'
  | 'verifying' | 'aborting' | 'verified' | 'resuming' | 'complete' | 'error';

/** Guided operator handoff to the supported networked deploy procedure. */
@Component({
  selector: 'app-review-host-update',
  standalone: true,
  imports: [DatePipe, PendingButtonDirective],
  templateUrl: './review-host-update.html',
  styleUrl: './review-host-update.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewHostUpdateComponent {
  private readonly http = inject(HttpClient);
  readonly target = input<ReviewUpdateTarget | null>(null);
  readonly captured = signal<ReviewUpdateTarget | null>(null);
  readonly stage = signal<Stage>('ready');
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly switchStartedAt = signal<string | null>(null);
  readonly proofVerified = signal(false);
  readonly drainRequested = signal(false);
  private eligibilityRequest = 0;
  readonly drainedSince = signal<string | null>(null);
  readonly active = computed(() => {
    const target = this.target();
    return this.captured() ?? (target?.updatePending === false ? null : target);
  });
  readonly progressIndex = computed(() => {
    switch (this.stage()) {
      case 'checking': case 'unavailable': case 'ready': case 'draining': case 'aborting': return 0;
      case 'blocked': return 1;
      case 'quiet': case 'switching': return 2;
      case 'verifying': return 3;
      case 'verified': case 'resuming': return 4;
      case 'complete': return 5;
      case 'error': return this.proofVerified() ? 4 : this.switchStartedAt() ? 3 : this.drainRequested() ? 1 : 0;
    }
  });
  stepState(index: number): 'pending' | 'current' | 'done' {
    return index < this.progressIndex() ? 'done' : index === this.progressIndex() ? 'current' : 'pending';
  }
  stepLabel(index: number): string {
    const state = this.stepState(index);
    return state === 'done' ? 'Done' : state === 'current' ? 'Current' : 'Next';
  }
  private readonly resetForHost = effect(() => {
    const incoming = this.target();
    const captured = this.captured();
    if (incoming && captured && incoming.clientId !== captured.clientId) this.reset();
  });
  private restoredFor: string | null = null;
  /**
   * AGT-3852: a reload or a second operator resumes at the step the server
   * drain status and recorded switch start imply, not at step 1.
   */
  private readonly restoreFromServer = effect(() => {
    const target = this.target();
    if (!target || target.clientId === this.restoredFor) return;
    this.restoredFor = target.clientId;
    untracked(() => void this.checkEligibility(target));
  });

  private url(target: ReviewUpdateTarget): string {
    return `/api/clients/${encodeURIComponent(target.clientId)}`;
  }

  async checkEligibility(target = this.target()): Promise<void> {
    if (!target) return;
    const request = ++this.eligibilityRequest;
    this.stage.set('checking');
    this.busy.set(true);
    this.message.set(null);
    try {
      const status = await firstValueFrom(this.http.get<ReviewDrainStatus>(this.url(target) + '/drain-status'));
      if (request !== this.eligibilityRequest || this.target()?.clientId !== target.clientId) return;
      if (status.draining && (target.updatePending !== false || status.switchStartedAt)) {
        this.captured.set(target);
        this.applyStatus(status);
      } else this.stage.set('ready');
    } catch (error) {
      if (request !== this.eligibilityRequest) return;
      this.failDrainAccess(error, 'Could not check the review identity. Check its connection and retry.');
    } finally {
      if (request === this.eligibilityRequest) this.busy.set(false);
    }
  }

  private failDrainAccess(error: unknown, fallback: string): void {
    if (error instanceof HttpErrorResponse && error.status === 404) {
      this.stage.set('unavailable');
      this.message.set('This review host has no registered client identity, so Studio cannot drain it here.');
      return;
    }
    this.fail(fallback);
  }

  async start(): Promise<void> {
    const target = this.target();
    if (!target || this.busy() || this.stage() !== 'ready') return;
    if (!target.expectedCommit || !target.expectedImageRef?.match(/@sha256:[0-9a-f]{64}$/i)) {
      this.message.set('Expected commit and image digest are required before this update can be verified.');
      return;
    }
    this.captured.set(target);
    this.stage.set('draining');
    this.busy.set(true);
    this.message.set(null);
    try {
      await firstValueFrom(this.http.post(this.url(target) + '/drain', {}));
      this.drainRequested.set(true);
      await this.readQuietPoint(target);
    } catch (error) {
      this.failDrainAccess(error, 'Could not drain or read the host. Check its connection and retry.');
    } finally {
      this.busy.set(false);
    }
  }

  async checkQuiet(): Promise<void> {
    const target = this.captured();
    if (!target || this.busy()) return;
    this.busy.set(true);
    try { await this.readQuietPoint(target); }
    catch (error) { this.failDrainAccess(error, 'Could not read the host drain status. Retry before switching.'); }
    finally { this.busy.set(false); }
  }

  private async readQuietPoint(target: ReviewUpdateTarget): Promise<boolean> {
    const status = await firstValueFrom(this.http.get<ReviewDrainStatus>(this.url(target) + '/drain-status'));
    return this.applyStatus(status) === 'quiet';
  }

  /** Mirrors the server drain status; a switch another operator started wins. */
  private applyStatus(status: ReviewDrainStatus): string {
    const { step, reason } = reviewUpdateStepFromStatus(status);
    if (status.draining) this.drainRequested.set(true);
    this.drainedSince.set(status.draining ? status.drainRequestedAt ?? null : null);
    this.switchStartedAt.set(status.switchStartedAt ?? null);
    this.stage.set(step === 'ready' ? 'blocked' : step);
    this.message.set(step === 'ready' ? 'The review host is not drained.' : reason);
    return step;
  }

  async beginSwitch(): Promise<void> {
    const target = this.captured();
    if (!target || this.busy()) return;
    this.busy.set(true);
    try {
      // The server records the start only at its quiet point, so every
      // operator and a reload read the same verification baseline.
      const status = await firstValueFrom(this.http.post<ReviewDrainStatus>(this.url(target) + '/deploy-switch', {}));
      if (this.applyStatus(status) !== 'switching') return;
      this.message.set('Run the review host steps in the deploy procedure, then check for a new heartbeat.');
    } catch (error) {
      const refused = error instanceof HttpErrorResponse && error.status === 409
        ? (error.error as { status?: ReviewDrainStatus } | null)?.status : undefined;
      if (refused) this.applyStatus(refused);
      else this.failDrainAccess(error, 'Could not confirm the quiet point. The host switch is refused.');
    }
    finally { this.busy.set(false); }
  }

  async verify(): Promise<void> {
    const target = this.captured();
    const switchedAfter = this.switchStartedAt();
    if (!target || !switchedAfter || this.busy()) return;
    this.busy.set(true);
    this.stage.set('verifying');
    try {
      const snapshot = await firstValueFrom(this.http.get<HostReleaseDriftSnapshot>(
        '/api/v1/management/host-releases'));
      if (!reviewHeartbeatProvesUpdate(snapshot, target, switchedAfter)) {
        this.stage.set('switching');
        this.message.set('Waiting for a new review host heartbeat with the expected commit and image digest.');
        return;
      }
      this.stage.set('verified');
      this.proofVerified.set(true);
      this.message.set('A new heartbeat proves the expected commit and image digest. Resume this host.');
    } catch { this.fail('Could not read the host release report. Retry verification.'); }
    finally { this.busy.set(false); }
  }

  async resume(): Promise<void> {
    const target = this.captured();
    if (!target || !this.proofVerified() || this.busy()) return;
    this.busy.set(true);
    this.stage.set('resuming');
    try {
      await firstValueFrom(this.http.post(this.url(target) + '/resume', {}));
      const status = await firstValueFrom(this.http.get<ReviewDrainStatus>(this.url(target) + '/drain-status'));
      if (status.draining) {
        this.fail('The host still reports draining. Check admission and retry resume.');
        return;
      }
      this.stage.set('complete');
      this.message.set('Review host update complete. The new heartbeat was verified and admission resumed.');
    } catch (error) { this.failDrainAccess(error, 'Could not confirm resumed admission. Retry resume.'); }
    finally { this.busy.set(false); }
  }

  async cancel(): Promise<void> {
    const target = this.captured();
    if (!target || this.switchStartedAt() || this.busy()) return;
    this.busy.set(true);
    this.stage.set('aborting');
    try {
      // AGT-3930: the server refuses the cancel once any operator recorded the
      // switch start, so a stale session cannot erase that baseline.
      const status = await firstValueFrom(this.http.post<ReviewDrainStatus>(this.url(target) + '/deploy-cancel', {}));
      if (status.draining) {
        this.fail('The host still reports draining. Retry cancellation.');
        return;
      }
      this.reset();
    } catch (error) {
      const refused = error instanceof HttpErrorResponse && error.status === 409
        ? (error.error as { status?: ReviewDrainStatus } | null)?.status : undefined;
      if (refused && this.applyStatus(refused) === 'switching')
        this.message.set('Another operator started the host switch, so cancel is closed. Run the review host steps in the deploy procedure, then check for a new heartbeat.');
      else if (!refused) this.failDrainAccess(error, 'Could not resume the host after cancellation. Retry cancellation.');
    }
    finally { this.busy.set(false); }
  }

  private fail(message: string): void {
    this.stage.set('error');
    this.message.set(message);
  }

  reset(): void {
    this.captured.set(null);
    this.switchStartedAt.set(null);
    this.proofVerified.set(false);
    this.drainRequested.set(false);
    this.drainedSince.set(null);
    this.message.set(null);
    this.stage.set('ready');
  }
}
