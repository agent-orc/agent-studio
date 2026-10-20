import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CredentialInventoryService } from '../credential-inventory.service';
import { WorkspaceOverlaysService } from '../../shell/state/workspace-overlays.service';
import { CodexSignInDialogService } from '../../remote-hosts/services/codex-sign-in-dialog.service';
import { ProviderAuthStatusService } from '../../remote-hosts/services/provider-auth-status.service';
import type { CredentialInventoryItem } from '../credential-inventory.service';

@Component({
  selector: 'app-credential-inventory',
  standalone: true,
  templateUrl: './credential-inventory.component.html',
  styleUrl: './credential-inventory.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CredentialInventoryComponent {
  readonly service = inject(CredentialInventoryService);
  private readonly overlays = inject(WorkspaceOverlaysService);
  private readonly codexSignIn = inject(CodexSignInDialogService);
  private readonly providerAuth = inject(ProviderAuthStatusService);
  readonly host = signal('all');
  readonly kind = signal('all');
  readonly state = signal('all');
  readonly hosts = computed(() => [...new Set(this.service.inventory()?.items.map(item => item.hostId) ?? [])].sort());
  readonly kinds = computed(() => [...new Set(this.service.inventory()?.items.map(item => item.kind) ?? [])].sort());
  readonly items = computed(() => (this.service.inventory()?.items ?? []).filter(item =>
    (this.host() === 'all' || item.hostId === this.host()) &&
    (this.kind() === 'all' || item.kind === this.kind()) &&
    (this.state() === 'all' || item.state === this.state())));
  readonly attention = computed(() => this.service.inventory()?.items.filter(item =>
    item.state === 'missing' || item.state === 'expiring' || item.state === 'unexpected').length ?? 0);

  constructor() { this.service.load(); this.providerAuth.start(); }

  openActions(category: 'provider' | 'repository' | 'service' | 'human'): void {
    this.overlays.select(category === 'service' || category === 'human' ? 'task-server' : 'remote-hosts');
  }

  lastChecked(item: CredentialInventoryItem): string {
    const badge = this.providerAuth.statuses().find(status =>
      status.provider === 'codex' && status.hostId === item.hostId);
    return badge?.advertisedAt ?? 'not checked';
  }

  loginState(item: CredentialInventoryItem): string {
    const badge = this.providerAuth.statuses().find(status =>
      status.provider === 'codex' && status.hostId === item.hostId);
    if (!badge) return 'unknown';
    if (badge.state === 'ok') return 'ok';
    if (badge.state === 'limited') return 'rate limited';
    if (badge.healthOutcome === 'credential_invalid') return 'expired';
    return badge.state === 'expiring' ? 'expiring' : 'missing';
  }

  signIn(item: CredentialInventoryItem): void {
    const badge = this.providerAuth.statuses().find(status =>
      status.provider === 'codex' && status.hostId === item.hostId);
    this.codexSignIn.open({ hostId: item.hostId, runnerId: badge?.runnerId ?? item.hostId,
      hostName: badge?.hostName ?? item.hostId, aliases: badge?.aliases ?? [item.hostId],
      baselineAdvertisedAt: badge?.advertisedAt ?? null });
  }
}
