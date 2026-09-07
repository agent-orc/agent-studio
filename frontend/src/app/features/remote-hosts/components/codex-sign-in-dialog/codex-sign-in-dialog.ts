import { ChangeDetectionStrategy, Component, OnDestroy, effect, inject, signal, untracked } from '@angular/core';
import { Subscription, switchMap, takeWhile, timer } from 'rxjs';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { NotificationService } from '../../../../services/notification.service';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import type {
  CodexSignInStartResponse,
  CodexSignInStatusResponse,
  CodexSignInTarget,
} from '../../models/provider-auth.model';
import { CodexSignInDialogService } from '../../services/codex-sign-in-dialog.service';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';

type CodexSignInPhase = 'starting' | 'pending' | 'verifying' | 'failed';

@Component({
  selector: 'app-codex-sign-in-dialog',
  standalone: true,
  imports: [DialogComponent],
  templateUrl: './codex-sign-in-dialog.html',
  styleUrl: './codex-sign-in-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CodexSignInDialogComponent implements OnDestroy {
  private readonly dialog = inject(CodexSignInDialogService);
  private readonly providerAuth = inject(ProviderAuthStatusService);
  private readonly notifications = inject(NotificationService);
  private polling: Subscription | null = null;
  private launchedKey: string | null = null;

  readonly request = this.dialog.request;
  readonly phase = signal<CodexSignInPhase>('starting');
  readonly session = signal<CodexSignInStartResponse | null>(null);
  readonly detail = signal('Starting Codex on the execution host…');
  readonly copyLabel = signal('Copy code');

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

  async copyCode(): Promise<void> {
    const code = this.session()?.userCode;
    if (!code) return;
    const copied = await copyTextToClipboard(code);
    this.copyLabel.set(copied ? 'Copied' : 'Copy failed');
  }

  private launch(request: CodexSignInTarget): void {
    const sshTarget = request.sshTarget?.trim();
    this.polling?.unsubscribe();
    this.polling = null;
    this.session.set(null);
    this.copyLabel.set('Copy code');
    this.phase.set('starting');
    this.detail.set(`Starting a host-owned Codex device sign-in on ${request.hostName}…`);
    if (!sshTarget) {
      this.fail('This execution host has no SSH target. Open its setup dialog and configure the host address first.');
      return;
    }

    this.providerAuth.startCodexSignIn(request.hostId, sshTarget).subscribe({
      next: session => {
        this.session.set(session);
        this.phase.set('pending');
        this.detail.set('Complete the browser flow. This window will detect completion automatically.');
        this.poll(request, session);
      },
      error: error => this.fail(errorDetail(error, 'Codex device sign-in could not be started.')),
    });
  }

  private poll(request: CodexSignInTarget, session: CodexSignInStartResponse): void {
    this.polling = timer(500, 1_000).pipe(
      switchMap(() => this.providerAuth.codexSignInStatus(request.hostId, session.handle)),
      takeWhile(status => status.state === 'pending', true),
    ).subscribe({
      next: status => this.acceptStatus(request, status),
      error: error => this.fail(errorDetail(error, 'Codex sign-in status could not be read.')),
    });
  }

  private acceptStatus(request: CodexSignInTarget, status: CodexSignInStatusResponse): void {
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
    this.detail.set('Codex confirmed the login. Waiting for a fresh runner provider probe…');
    this.polling = this.providerAuth.waitForFreshProbe(
      'codex',
      request.aliases,
      request.baselineAdvertisedAt,
    ).subscribe({
      next: badge => {
        if (badge.state !== 'ok') {
          this.fail(`The fresh runner probe still reports ${badge.state}: ${badge.detail}`);
          return;
        }
        this.notifications.success(
          `Codex authentication is available on ${badge.hostName}. Ready cards can resume.`,
          'Codex sign-in complete',
        );
        this.dialog.refreshHosts();
        this.close();
      },
      error: () => this.fail('Codex signed in, but a fresh OK provider probe did not arrive in time. Re-probe the host or retry.'),
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
    this.detail.set('Starting Codex on the execution host…');
    this.copyLabel.set('Copy code');
  }
}

function errorDetail(error: unknown, fallback: string): string {
  const candidate = error as { error?: { message?: string }; message?: string } | null;
  return candidate?.error?.message?.trim() || candidate?.message?.trim() || fallback;
}
