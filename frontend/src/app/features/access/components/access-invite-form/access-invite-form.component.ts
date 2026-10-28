import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { formatAccessTime, type IamGrant } from '../../models/access.model';
import { AccessService } from '../../services/access.service';
import { AccessGrantFormComponent } from '../access-grant-form/access-grant-form.component';

/**
 * Invites a person through the existing invitation flow
 * (`POST /api/auth/invitations`) with one initial role binding. The sign-in
 * link is shown once; it is valid for 24 hours.
 */
@Component({
  selector: 'app-access-invite-form',
  standalone: true,
  imports: [AccessGrantFormComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-invite-form.component.html',
  styleUrl: './access-invite-form.component.scss',
})
export class AccessInviteFormComponent {
  readonly access = inject(AccessService);
  readonly username = signal('');
  readonly displayName = signal('');
  readonly grant = signal<IamGrant | null>(null);
  readonly link = signal<{ url: string; username: string; expiresAt: string } | null>(null);

  canInvite(): boolean {
    return this.username().trim().length >= 3 && !!this.grant() && !this.access.busy();
  }

  async invite(): Promise<void> {
    const grant = this.grant();
    if (!grant || !this.canInvite()) return;
    const result = await this.access.invite(this.username().trim(), this.displayName().trim(), [grant]);
    if (!result) return;
    const origin = typeof location === 'undefined' ? '' : location.origin;
    this.link.set({ url: origin + result.signInLink, username: result.user.username, expiresAt: formatAccessTime(result.expiresAt) });
    this.username.set('');
    this.displayName.set('');
  }
}
