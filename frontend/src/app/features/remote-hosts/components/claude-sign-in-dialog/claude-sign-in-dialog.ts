import { ChangeDetectionStrategy, Component, OnDestroy, effect, inject, signal, untracked } from '@angular/core';
import { Subscription, switchMap, takeWhile, timer } from 'rxjs';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { NotificationService } from '../../../../services/notification.service';
import type {
  ClaudeSignInStartResponse,
  ClaudeSignInStatusResponse,
  ClaudeSignInTarget,
} from '../../models/provider-auth.model';
import { ClaudeSignInDialogService } from '../../services/claude-sign-in-dialog.service';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';

type ClaudeSignInPhase = 'starting' | 'pending' | 'verifying' | 'failed';

@Component({
  selector: 'app-claude-sign-in-dialog',
  standalone: true,
  imports: [DialogComponent],
  templateUrl: './claude-sign-in-dialog.html',
  styleUrl: './claude-sign-in-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ClaudeSignInDialogComponent implements OnDestroy {
  private readonly dialog = inject(ClaudeSignInDialogService);
  private readonly providerAuth = inject(ProviderAuthStatusService);
  private readonly notifications = inject(NotificationService);
  private polling: Subscription | null = null;
  private launchedKey: string | null = null;

  readonly request = this.dialog.request;
  readonly phase = signal<ClaudeSignInPhase>('starting');
  readonly session = signal<ClaudeSignInStartResponse | null>(null);
  readonly detail = signal('Starting Claude on the execution host…');

  private readonly requestEffect = effect(() => {
    const request = this.request();
    if (!request) {
      untracked(() => this.reset());
      return;
    }
    const key = `${request.hostId}|${request.sshTarget}|${request.baselineAdvertisedAt ?? ''}`;
    if (this.launchedKey === key) return;
    this.launchedKey = key;
    untracked(() => this.launch(request));
  });

  ngOnDestroy(): void {
    this.polling?.unsubscribe();
  }

  close(): void {
    this.polling?.unsubscribe();
    this.polling = null;
    this.dialog.close();
  }

  retry(): void {
    const request = this.request();
    if (!request) return;
    this.launch(request);
  }

  private launch(request: ClaudeSignInTarget): void {
    const sshTarget = request.sshTarget?.trim();
    this.polling?.unsubscribe();
    this.polling = null;
    this.session.set(null);
    this.phase.set('starting');
    this.detail.set(`Starting a host-owned Claude sign-in on ${request.hostName}…`);
    if (!sshTarget) {
      this.fail('This execution host has no SSH target. Open its setup dialog and configure the host address first.');
      return;
    }

    this.providerAuth.startClaudeSignIn(request.hostId, sshTarget).subscribe({
      next: session => {
        this.session.set(session);
        this.phase.set('pending');
        this.detail.set('Complete the browser flow. This window will detect completion automatically.');
        this.poll(request, session);
      },
      error: error => this.fail(errorDetail(error, 'Claude sign-in could not be started.')),
    });
  }

  private poll(request: ClaudeSignInTarget, session: ClaudeSignInStartResponse): void {
    this.polling = timer(500, 1_000).pipe(
      switchMap(() => this.providerAuth.claudeSignInStatus(request.hostId, session.handle)),
      takeWhile(status => status.state === 'pending', true),
    ).subscribe({
      next: status => this.acceptStatus(request, status),
      error: error => this.fail(errorDetail(error, 'Claude sign-in status could not be read.')),
    });
  }

  private acceptStatus(request: ClaudeSignInTarget, status: ClaudeSignInStatusResponse): void {
    this.detail.set(status.detail);
    if (status.state === 'pending') return;
    this.polling?.unsubscribe();
    this.polling = null;
    this.session.set(null);
    if (status.state === 'failed') {
      this.phase.set('failed');
      return;
    }

    this.phase.set('verifying');
    this.detail.set('Claude confirmed the login. Waiting for a fresh runner provider probe…');
    this.polling = this.providerAuth.waitForFreshProbe(
      'claude',
      request.aliases,
      request.baselineAdvertisedAt,
    ).subscribe({
      next: badge => {
        if (badge.state !== 'ok') {
          this.fail(`The fresh runner probe still reports ${badge.state}: ${badge.detail}`);
          return;
        }
        this.notifications.success(
          `Claude authentication is available on ${badge.hostName}. Ready cards can resume.`,
          'Claude sign-in complete',
        );
        this.dialog.refreshHosts();
        this.close();
      },
      error: () => this.fail('Claude signed in, but a fresh OK provider probe did not arrive in time. Re-probe the host or retry.'),
    });
  }

  private fail(detail: string): void {
    this.polling?.unsubscribe();
    this.polling = null;
    this.session.set(null);
    this.phase.set('failed');
    this.detail.set(detail);
  }

  private reset(): void {
    this.polling?.unsubscribe();
    this.polling = null;
    this.launchedKey = null;
    this.phase.set('starting');
    this.session.set(null);
    this.detail.set('Starting Claude on the execution host…');
  }
}

function errorDetail(error: unknown, fallback: string): string {
  const candidate = error as { error?: { message?: string }; message?: string } | null;
  return candidate?.error?.message?.trim() || candidate?.message?.trim() || fallback;
}
