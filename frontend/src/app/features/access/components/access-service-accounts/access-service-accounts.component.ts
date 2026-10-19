import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import {
  formatAccessTime,
  scopeLabel,
  type IamGrant,
  type OneTimeServiceAccountToken,
  type ServiceAccount,
  type ServiceAccountToken,
} from '../../models/access.model';
import { AccessService } from '../../services/access.service';
import { AccessGrantFormComponent } from '../access-grant-form/access-grant-form.component';

const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * Non-human principals with their own API tokens. A token is shown once at
 * creation; the store keeps only its hash, expiry, last use, and revocation.
 */
@Component({
  selector: 'app-access-service-accounts',
  standalone: true,
  imports: [AccessGrantFormComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-service-accounts.component.html',
  styleUrl: './access-service-accounts.component.scss',
})
export class AccessServiceAccountsComponent {
  readonly access = inject(AccessService);
  readonly name = signal('');
  readonly description = signal('');
  readonly grant = signal<IamGrant | null>(null);
  readonly tokenDays = signal(90);
  readonly issued = signal<OneTimeServiceAccountToken | null>(null);
  readonly accounts = computed(() => this.access.overview()?.serviceAccounts ?? []);
  readonly lifetimes: readonly number[] = [30, 90, 365];
  readonly time = formatAccessTime;

  bindings(account: ServiceAccount): string[] {
    const overview = this.access.overview();
    if (!overview) return [];
    return overview.bindings
      .filter(binding => binding.principalKind === 'service-account' && binding.principalId === account.id)
      .map(binding => `${overview.roles.find(role => role.id === binding.role)?.displayName ?? binding.role} · ${scopeLabel(binding.scope, this.access.projects())}`);
  }

  activeTokens(account: ServiceAccount): ServiceAccountToken[] {
    return account.tokens.filter(token => !token.revokedAt);
  }

  async create(): Promise<void> {
    const name = this.name().trim();
    const grant = this.grant();
    if (!name || this.access.busy()) return;
    if (await this.access.createServiceAccount(name, this.description().trim(), grant ? [grant] : [])) {
      this.name.set('');
      this.description.set('');
    }
  }

  async issue(account: ServiceAccount): Promise<void> {
    if (this.access.busy()) return;
    const expiresAt = new Date(Date.now() + this.tokenDays() * DAY_MS).toISOString();
    this.issued.set(await this.access.createToken(account.id, `${account.name} token`, expiresAt));
  }

  async revoke(account: ServiceAccount, token: ServiceAccountToken): Promise<void> {
    if (this.access.busy()) return;
    await this.access.revokeToken(account.id, token.id);
  }

  async disable(account: ServiceAccount): Promise<void> {
    if (this.access.busy()) return;
    await this.access.disableServiceAccount(account.id);
  }

  setLifetime(value: string): void {
    const days = Number(value);
    if (this.lifetimes.includes(days)) this.tokenDays.set(days);
  }
}
