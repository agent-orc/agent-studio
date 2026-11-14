import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { AuthService } from '../../services/auth.service';

/** Shell notice after a recovery-code sign-in: how many codes remain and where to bind a new authenticator. */
@Component({
  selector: 'app-recovery-sign-in-notice',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './recovery-sign-in-notice.component.html',
  styleUrl: './recovery-sign-in-notice.component.scss',
})
export class RecoverySignInNoticeComponent {
  private readonly auth = inject(AuthService);
  readonly remaining = computed(() => this.auth.status()?.recoveryCodesRemaining ?? null);
}
