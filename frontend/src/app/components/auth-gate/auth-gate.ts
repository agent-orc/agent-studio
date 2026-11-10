import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, firstValueFrom } from 'rxjs';
import { AuthService } from '../../services/auth.service';
import { createPasskey, getPasskey } from '../../services/webauthn';
export { AuthService } from '../../services/auth.service';

/** Shown when a gate request never reached the server. */
export const AUTH_GATE_RECONNECTING = 'Studio is reconnecting; try again.';

/**
 * The text for a failed gate request. Status 0 means the server never answered:
 * the offline guard refused the request, or the network failed. Say so instead
 * of blaming the code or password the user typed.
 */
export function authGateFailure(error: unknown, fallback: string): string {
  if (error instanceof HttpErrorResponse && error.status === 0) return AUTH_GATE_RECONNECTING;
  if (error instanceof HttpErrorResponse && error.error?.code === 'invalid-second-factor')
    return 'The authenticator code is invalid.';
  if (error instanceof HttpErrorResponse) return error.error?.message ?? fallback;
  return error instanceof Error ? error.message : fallback;
}

@Component({
  selector: 'app-auth-gate',
  imports: [FormsModule],
  templateUrl: './auth-gate.html',
  styleUrl: './auth-gate.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuthGateComponent {
  readonly auth = inject(AuthService);
  readonly inviteToken = new URLSearchParams(window.location.search).get('invite');
  username = '';
  displayName = '';
  password = '';
  bootstrapCode = '';
  newPassword = '';
  secondFactorCode = '';
  readonly totpSecret = signal('');
  readonly recoveryCodes = signal<string[]>([]);
  readonly submitting = signal(false);
  readonly error = signal('');

  submit(): void {
    const status = this.auth.status();
    if (status?.secondFactorEnrollmentRequired && !status.user?.mustChangePassword) {
      this.confirmTotp();
      return;
    }
    this.submitting.set(true);
    this.error.set('');
    const request: Observable<unknown> = this.inviteToken
      ? this.auth.acceptInvitation(this.inviteToken, this.newPassword)
      : status?.bootstrapRequired
      ? this.auth.bootstrap(this.username, this.password, this.displayName, this.bootstrapCode)
      : status?.user?.mustChangePassword
        ? this.auth.changePassword(this.password, this.newPassword)
        : this.auth.login(this.username, this.password, this.secondFactorCode);
    request.subscribe({
      next: () => {
        if (this.inviteToken) window.history.replaceState(null, '', window.location.pathname);
        this.password = ''; this.newPassword = ''; this.bootstrapCode = ''; this.secondFactorCode = ''; this.submitting.set(false);
      },
      error: (error: unknown) => {
        this.error.set(authGateFailure(error, 'Sign-in failed.'));
        this.submitting.set(false);
      },
    });
  }

  beginTotp(): void {
    this.submitting.set(true);
    this.error.set('');
    this.auth.beginTotp().subscribe({
      next: result => { this.totpSecret.set(result.secret); this.submitting.set(false); },
      error: (error: unknown) => { this.error.set(authGateFailure(error, 'Authenticator setup failed.')); this.submitting.set(false); },
    });
  }

  private confirmTotp(): void {
    this.submitting.set(true);
    this.error.set('');
    this.auth.confirmTotp(this.secondFactorCode).subscribe({
      next: result => { this.recoveryCodes.set(result.recoveryCodes); this.secondFactorCode = ''; this.submitting.set(false); },
      error: (error: unknown) => {
        this.error.set(error instanceof HttpErrorResponse && error.status === 400
          ? 'The authenticator code is invalid.'
          : authGateFailure(error, 'Authenticator verification failed.'));
        this.submitting.set(false);
      },
    });
  }

  acknowledgeFactorRecoveryCodes(): void {
    this.recoveryCodes.set([]);
    this.auth.finishFactorEnrollment();
  }

  async registerPasskey(): Promise<void> {
    this.submitting.set(true);
    this.error.set('');
    try {
      const options = await firstValueFrom(this.auth.passkeyRegistrationOptions());
      const proof = await createPasskey(options);
      const result = await firstValueFrom(this.auth.registerPasskey(proof));
      this.recoveryCodes.set(result.recoveryCodes);
    } catch (error) {
      this.error.set(authGateFailure(error, 'Passkey setup failed.'));
    } finally { this.submitting.set(false); }
  }

  async signInWithPasskey(): Promise<void> {
    this.submitting.set(true);
    this.error.set('');
    try {
      const options = await firstValueFrom(this.auth.passkeyLoginOptions(this.username));
      const proof = await getPasskey(options);
      await firstValueFrom(this.auth.loginWithPasskey(this.username, this.password, proof));
      this.password = '';
    } catch (error) {
      this.error.set(authGateFailure(error, 'Passkey sign-in failed.'));
    } finally { this.submitting.set(false); }
  }

  acknowledgeRecoveryCode(): void { this.auth.acknowledgeRecoveryCode(); }
}
