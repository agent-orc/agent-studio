import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { SecretRevealComponent } from '../../../../components/secret-reveal/secret-reveal.component';
import { formatAccessTime, type IamGrant } from '../../models/access.model';
import { AccessService } from '../../services/access.service';
import { AccessGrantFormComponent } from '../access-grant-form/access-grant-form.component';

/**
 * Invites a person through the existing invitation flow
 * (`POST /api/auth/invitations`) with one initial role binding. The sign-in
 * link is shown once, in the shared secret reveal; it is valid for 24 hours.
 */
@Component({
  selector: 'app-access-invite-form',
  standalone: true,
  imports: [AccessGrantFormComponent, SecretRevealComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-invite-form.component.html',
  styleUrl: './access-invite-form.component.scss',
})
export class AccessInviteFormComponent {
  readonly access = inject(AccessService);
  readonly username = signal('');
  readonly displayName = signal('');
  readonly grant = signal<IamGrant | null>(null);
  readonly link = signal<{ url: string; username: string; expiresAt: string; factor: string } | null>(null);
  readonly pending = signal<{ username: string; role: string } | null>(null);

  constructor() {
    effect(() => {
      if (this.access.state() === 'denied') this.link.set(null);
    });
  }

  canInvite(): boolean {
    return this.username().trim().length >= 3 && !!this.grant() && !this.access.busy() && !this.pending();
  }

  async invite(): Promise<void> {
    const grant = this.grant();
    if (!grant || !this.canInvite()) return;
    const username = this.username().trim();
    const role = this.access.overview()?.roles.find(item => item.id === grant.role)?.displayName ?? grant.role;
    this.link.set(null);
    this.pending.set({ username, role });
    let result: Awaited<ReturnType<AccessService['invite']>>;
    try {
      result = await this.access.invite(username, this.displayName().trim(), [grant]);
    } finally {
      this.pending.set(null);
    }
    if (!result) return;
    const origin = typeof location === 'undefined' ? '' : location.origin;
    const factor = result.user.role === 'owner' || result.user.role === 'operator'
      ? 'A second factor is required before using Studio.'
      : grant.role === 'approver'
        ? 'A second factor is required to approve production releases. Set one up after acceptance.'
        : 'A second factor is recommended after acceptance.';
    this.link.set({ url: origin + result.signInLink, username: result.user.username, expiresAt: formatAccessTime(result.expiresAt), factor });
    this.username.set('');
    this.displayName.set('');
  }
}
