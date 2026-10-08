import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { AuthSessionState } from '../../../../services/auth.service';
import type { RemoteHost } from '../../models/remote-host.model';
import { credentialDue, credentialStatus, type CredentialView } from '../../models/credential-view.model';
import { CodexSignInDialogService } from '../../services/codex-sign-in-dialog.service';
import { ClaudeSignInDialogService } from '../../services/claude-sign-in-dialog.service';
import { RemoteHostDetailSummaryComponent } from '../remote-host-detail-summary/remote-host-detail-summary';

@Component({
  selector: 'app-host-credentials',
  standalone: true,
  imports: [DatePipe, RemoteHostDetailSummaryComponent],
  templateUrl: './host-credentials.html',
  styleUrl: './host-credentials.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HostCredentialsComponent {
  private readonly auth = inject(AuthSessionState);
  private readonly codexSignIn = inject(CodexSignInDialogService);
  private readonly claudeSignIn = inject(ClaudeSignInDialogService);
  readonly host = input.required<RemoteHost>();
  readonly credentials = input<readonly CredentialView[]>([]);
  readonly expanded = signal(false);
  readonly hostCredentials = computed(() => this.credentials().filter(credential =>
    credential.hostId === (this.host().capacityHostId || this.host().id)
    || credential.hostId === this.host().clientId));
  readonly canRenew = computed(() => this.auth.status()?.profile === 'local'
    || ['owner', 'operator'].includes(this.auth.status()?.user?.role ?? ''));
  readonly credentialDue = credentialDue;
  readonly credentialStatus = credentialStatus;

  toggle(): void { this.expanded.update(value => !value); }

  renewCredential(view: CredentialView): void {
    if (!this.canRenew() || view.outcome !== 'credential_invalid') return;
    const target = {
      hostId: view.hostId, runnerId: this.host().clientId, hostName: this.host().name,
      aliases: [this.host().id, this.host().clientId], sshTarget: this.host().address,
      baselineAdvertisedAt: null,
    };
    if (view.renewalAction === 'codex-sign-in') this.codexSignIn.open(target);
    if (view.renewalAction === 'claude-sign-in') this.claudeSignIn.open(target);
  }
}
