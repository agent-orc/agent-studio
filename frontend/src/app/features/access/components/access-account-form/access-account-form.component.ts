import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import type { IamGrant } from '../../models/access.model';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { AccessGrantFormComponent } from '../access-grant-form/access-grant-form.component';

/** Creates a service account with one first role and opens it, so its first token can be issued right away. */
@Component({
  selector: 'app-access-account-form',
  standalone: true,
  imports: [AccessGrantFormComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-account-form.component.html',
  styleUrl: './access-account-form.component.scss',
})
export class AccessAccountFormComponent {
  readonly access = inject(AccessService);
  private readonly selection = inject(AccessSelectionService);
  readonly name = signal('');
  readonly description = signal('');
  readonly grant = signal<IamGrant | null>(null);

  async create(): Promise<void> {
    const name = this.name().trim();
    const grant = this.grant();
    if (!name || this.access.busy()) return;
    const account = await this.access.createServiceAccount(name, this.description().trim(), grant ? [grant] : []);
    if (!account) return;
    this.name.set('');
    this.description.set('');
    if (account.id) this.selection.open('service-account', account.id);
  }
}
