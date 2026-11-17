import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import type { Observable } from 'rxjs';
import { AuthService, type AuthStatus } from '../../services/auth.service';

/**
 * Creates the owner account, or signs in to it, in place on the local
 * profile. The local shell opens without a session, so an action that needs
 * a signed-in Owner (production approval, runner enrollment, Access) renders
 * this next to its refusal and continues on `signedIn`.
 */
@Component({
  selector: 'app-local-owner-sign-in',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './local-owner-sign-in.html',
  styleUrl: './local-owner-sign-in.scss',
})
export class LocalOwnerSignInComponent {
  private readonly auth = inject(AuthService);
  /** Shows the form at once instead of the button that opens it. */
  readonly startOpen = input(false);
  readonly signedIn = output<void>();

  readonly opened = signal(false);
  readonly open = computed(() => this.startOpen() || this.opened());
  readonly bootstrap = computed(() => this.auth.status()?.bootstrapRequired === true);
  readonly user = computed(() => this.auth.status()?.authenticated ? this.auth.status()?.user ?? null : null);
  readonly username = signal('');
  readonly displayName = signal('');
  readonly password = signal('');
  readonly secondFactorCode = signal('');
  readonly submitting = signal(false);
  readonly error = signal<string | null>(null);
  readonly fieldErrors = signal<Partial<Record<string, string[]>>>({});

  submit(): void {
    if (this.submitting()) return;
    const bootstrap = this.bootstrap();
    this.error.set(null);
    this.fieldErrors.set({});
    this.submitting.set(true);
    const request: Observable<AuthStatus> = bootstrap
      ? this.auth.bootstrap(this.username().trim(), this.password(), this.displayName().trim())
      : this.auth.login(this.username().trim(), this.password(), this.secondFactorCode().trim() || undefined);
    request.subscribe({
      next: () => {
        this.password.set('');
        this.secondFactorCode.set('');
        this.submitting.set(false);
        this.opened.set(false);
        this.signedIn.emit();
      },
      error: (error: unknown) => {
        const body = error instanceof HttpErrorResponse ? error.error : null;
        if (body?.errors) this.fieldErrors.set(body.errors);
        this.error.set(error instanceof HttpErrorResponse && error.status === 0
          ? 'Studio is reconnecting; try again.'
          : body?.message ?? (bootstrap ? 'The owner account could not be created.' : 'Sign-in failed.'));
        this.submitting.set(false);
      },
    });
  }
}
