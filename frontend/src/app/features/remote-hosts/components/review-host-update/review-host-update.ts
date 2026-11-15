import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PendingButtonDirective } from '../../../../components/async-feedback/pending-button.directive';
import type { HostReleaseDriftSnapshot } from '../../models/host-release-drift';
import { reviewHeartbeatProvesUpdate, reviewQuietPoint,
  type ReviewDrainStatus, type ReviewUpdateTarget } from '../../models/review-host-update';

type Stage = 'ready' | 'draining' | 'blocked' | 'quiet' | 'switching' | 'verifying' | 'aborting'
  | 'verified' | 'resuming' | 'complete' | 'error';

/** Guided operator handoff to the supported networked deploy procedure. */
@Component({
  selector: 'app-review-host-update',
  standalone: true,
  imports: [PendingButtonDirective],
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
  readonly active = computed(() => this.captured() ?? this.target());
  readonly progressIndex = computed(() => {
    switch (this.stage()) {
      case 'ready': case 'draining': case 'aborting': return 0;
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

  private url(target: ReviewUpdateTarget): string {
    return `/api/clients/${encodeURIComponent(target.clientId)}`;
  }

  async start(): Promise<void> {
    const target = this.target();
    if (!target || this.busy()) return;
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
    } catch {
      this.fail('Could not drain or read the host. Check its connection and retry.');
    } finally {
      this.busy.set(false);
    }
  }

  async checkQuiet(): Promise<void> {
    const target = this.captured();
    if (!target || this.busy()) return;
    this.busy.set(true);
    try { await this.readQuietPoint(target); }
    catch { this.fail('Could not read the host drain status. Retry before switching.'); }
    finally { this.busy.set(false); }
  }

  private async readQuietPoint(target: ReviewUpdateTarget): Promise<boolean> {
    const status = await firstValueFrom(this.http.get<ReviewDrainStatus>(this.url(target) + '/drain-status'));
    const point = reviewQuietPoint(status);
    this.stage.set(point.quiet ? 'quiet' : 'blocked');
    this.message.set(point.reason);
    return point.quiet;
  }

  async beginSwitch(): Promise<void> {
    const target = this.captured();
    if (!target || this.busy()) return;
    this.busy.set(true);
    try {
      if (!await this.readQuietPoint(target)) return;
      this.switchStartedAt.set(new Date().toISOString());
      this.stage.set('switching');
      this.message.set('Run the review host steps in the deploy procedure, then check for a new heartbeat.');
    } catch { this.fail('Could not confirm the quiet point. The host switch is refused.'); }
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
    } catch { this.fail('Could not confirm resumed admission. Retry resume.'); }
    finally { this.busy.set(false); }
  }

  async cancel(): Promise<void> {
    const target = this.captured();
    if (!target || this.switchStartedAt() || this.busy()) return;
    this.busy.set(true);
    this.stage.set('aborting');
    try {
      await firstValueFrom(this.http.post(this.url(target) + '/resume', {}));
      const status = await firstValueFrom(this.http.get<ReviewDrainStatus>(this.url(target) + '/drain-status'));
      if (status.draining) {
        this.fail('The host still reports draining. Retry cancellation.');
        return;
      }
      this.reset();
    } catch { this.fail('Could not resume the host after cancellation. Retry cancellation.'); }
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
    this.message.set(null);
    this.stage.set('ready');
  }
}
