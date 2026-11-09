import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import type { DeviceLoginCodeState } from '../../models/provider-auth.model';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';

/**
 * The code-return step of a device sign-in (AGT-3243). Headless `claude setup-token` waits for the
 * authorization code its callback page shows; the Owner pastes it here and the Task Server hands it
 * once to the claiming host. The typed value is dropped as soon as the server took it.
 */
@Component({
  selector: 'app-device-login-code-entry',
  standalone: true,
  templateUrl: './device-login-code-entry.html',
  styleUrl: './device-login-code-entry.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DeviceLoginCodeEntryComponent {
  private readonly providerAuth = inject(ProviderAuthStatusService);

  readonly handle = input.required<string>();
  readonly sessionKey = input.required<string>();
  readonly providerLabel = input.required<string>();
  readonly codeState = input<DeviceLoginCodeState | null | undefined>(null);

  readonly code = signal('');
  readonly submitting = signal(false);
  readonly error = signal<string | null>(null);
  /** The session this view sent a code for, so the field does not return before the next status poll. */
  private readonly sentFor = signal<string | null>(null);
  readonly sent = computed(() => this.sentFor() === this.handle()
    || this.codeState() === 'submitted' || this.codeState() === 'delivered');

  submit(): void {
    const code = this.code().trim();
    if (!code || this.submitting()) return;
    this.submitting.set(true);
    this.error.set(null);
    this.providerAuth.submitDeviceLoginCode(this.handle(), this.sessionKey(), code).subscribe({
      next: () => {
        this.code.set('');
        this.submitting.set(false);
        this.sentFor.set(this.handle());
      },
      error: (error: { error?: { message?: string } } | null) => {
        this.submitting.set(false);
        this.error.set(error?.error?.message?.trim()
          || 'The authorization code could not be sent. Check it and try again.');
      },
    });
  }
}
