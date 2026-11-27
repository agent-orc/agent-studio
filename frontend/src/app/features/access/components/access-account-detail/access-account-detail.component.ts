import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { InlineConfirmComponent } from '../../../../components/inline-confirm/inline-confirm.component';
import { PageSectionComponent } from '../../../../components/page-section/page-section.component';
import { RowComponent } from '@studio/ui/components/row/row.component';
import { SecretRevealComponent } from '../../../../components/secret-reveal/secret-reveal.component';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { formatAccessTime, principalName, type OneTimeServiceAccountToken, type ServiceAccountToken } from '../../models/access.model';
import { activeTokens, isApiToken } from '../../models/access-view.model';
import { ACCESS_CLOCK } from '../../services/access-clock';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { AccessBindingListComponent } from '../access-binding-list/access-binding-list.component';

const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * Detail of one service account or API token: its roles and its tokens in
 * one place. A new token is shown once at the top of the token list; the
 * store keeps only its hash, expiry, last use, and revocation. An API token
 * is replaced rather than reissued, so it offers no "New token".
 */
@Component({
  selector: 'app-access-account-detail',
  standalone: true,
  imports: [AccessBindingListComponent, InlineConfirmComponent, PageSectionComponent, RowComponent, SecretRevealComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-account-detail.component.html',
  styleUrl: './access-account-detail.component.scss',
})
export class AccessAccountDetailComponent {
  readonly access = inject(AccessService);
  readonly selection = inject(AccessSelectionService);
  private readonly now = inject(ACCESS_CLOCK);
  readonly accountId = input.required<string>();
  readonly tokenDays = signal(90);
  readonly issued = signal<OneTimeServiceAccountToken | null>(null);
  readonly lifetimes: readonly number[] = [30, 90, 365];
  readonly time = formatAccessTime;

  readonly account = computed(() => this.access.overview()?.serviceAccounts.find(candidate => candidate.id === this.accountId()) ?? null);
  readonly api = computed(() => { const account = this.account(); return !!account && isApiToken(account); });
  /** An API token is replaced rather than reissued, and a disabled account takes no token. */
  readonly canIssue = computed(() => { const account = this.account(); return !!account && !isApiToken(account) && !account.disabledAt; });
  readonly tokens = computed(() => { const account = this.account(); return account ? activeTokens(account) : []; });
  readonly creator = computed(() => {
    const id = this.account()?.createdBy;
    return id ? principalName('user', id, this.access.overview()) : '';
  });
  /** The roles an API token is bound to, with their description, so the Owner sees what it may do (OP-1). */
  readonly tokenRoles = computed(() => {
    const overview = this.access.overview();
    if (!overview || !this.api()) return [];
    const ids = [...new Set(overview.bindings
      .filter(binding => binding.principalKind === 'service-account' && binding.principalId === this.accountId())
      .map(binding => binding.role))];
    return ids.map(id => {
      const role = overview.roles.find(candidate => candidate.id === id);
      return { id, name: role?.displayName ?? id, description: role?.description ?? '' };
    });
  });

  constructor() {
    // A one-time token belongs to one account and one signed-in view: another row or a refusal drops it.
    effect(() => {
      this.accountId();
      untracked(() => this.issued.set(null));
    });
    effect(() => {
      if (this.access.state() === 'denied') this.issued.set(null);
    });
  }

  armKey(token: ServiceAccountToken): string {
    return `revoke-token:${token.id}`;
  }

  setLifetime(value: string): void {
    const days = Number(value);
    if (this.lifetimes.includes(days)) this.tokenDays.set(days);
  }

  async issue(): Promise<void> {
    const account = this.account();
    if (!account || this.access.busy()) return;
    const expiresAt = new Date(this.now() + this.tokenDays() * DAY_MS).toISOString();
    this.issued.set(await this.access.createToken(account.id, `${account.name} token`, expiresAt));
  }

  async revoke(token: ServiceAccountToken): Promise<void> {
    if (this.access.busy()) return;
    await this.access.revokeToken(this.accountId(), token.id);
  }

  async disable(): Promise<void> {
    if (this.access.busy()) return;
    await this.access.disableServiceAccount(this.accountId());
  }
}
