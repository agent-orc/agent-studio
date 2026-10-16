import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable } from 'rxjs';
import { AuthService } from '../../services/auth.service';
export { AuthService } from '../../services/auth.service';

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
  readonly submitting = signal(false);
  readonly error = signal('');

  submit(): void {
    const status = this.auth.status();
    this.submitting.set(true);
    this.error.set('');
    const request: Observable<unknown> = this.inviteToken
      ? this.auth.acceptInvitation(this.inviteToken, this.newPassword)
      : status?.bootstrapRequired
      ? this.auth.bootstrap(this.username, this.password, this.displayName, this.bootstrapCode)
      : status?.user?.mustChangePassword
        ? this.auth.changePassword(this.password, this.newPassword)
        : this.auth.login(this.username, this.password);
    request.subscribe({
      next: () => {
        if (this.inviteToken) window.history.replaceState(null, '', window.location.pathname);
        this.password = ''; this.newPassword = ''; this.bootstrapCode = ''; this.submitting.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.error.set(error?.error?.message ?? 'Sign-in failed.');
        this.submitting.set(false);
      },
    });
  }

  acknowledgeRecoveryCode(): void { this.auth.acknowledgeRecoveryCode(); }
}
