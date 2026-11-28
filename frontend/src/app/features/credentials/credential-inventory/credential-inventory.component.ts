import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { DisclosureMarkerComponent } from '@studio/ui/components/disclosure-marker/disclosure-marker.component';
import { githubGrantDependencies, githubGrantLabel, type GitHubOperationTarget } from '../github-operation-manifest';
import { HostNamePipe } from '../../../components/identity-chip/host-name.pipe';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CredentialInventoryService } from '../credential-inventory.service';
import { WorkspaceOverlaysService } from '../../shell/state/workspace-overlays.service';
import { CodexSignInDialogService } from '../../remote-hosts/services/codex-sign-in-dialog.service';
import { ProviderAuthStatusService } from '../../remote-hosts/services/provider-auth-status.service';
import type { CredentialInventoryItem } from '../credential-inventory.service';
import type { ProviderAuthBadge } from '../../remote-hosts/models/provider-auth.model';
import { StatusIndicatorComponent, type StatusTone } from '@studio/ui/components/status-indicator/status-indicator.component';

type CredentialState = CredentialInventoryItem['state'];

/** Inventory state per credential; tones follow docs/quality/design/status-indicator.md. */
const credentialStates: Readonly<Record<CredentialState, { tone: StatusTone; label: string }>> = {
  ok: { tone: 'success', label: 'OK' },
  expiring: { tone: 'warning', label: 'Expiring' },
  missing: { tone: 'danger', label: 'Missing' },
  unused: { tone: 'neutral', label: 'Unused' },
  unexpected: { tone: 'danger', label: 'Unexpected write access' },
};

const loginTones: Readonly<Record<string, StatusTone>> = {
  ok: 'success',
  'rate limited': 'warning',
  expiring: 'warning',
  expired: 'danger',
  missing: 'danger',
};

@Component({
  selector: 'app-credential-inventory',
  standalone: true,
  imports: [DatePipe, NgTemplateOutlet, DisclosureMarkerComponent, HostNamePipe, StatusIndicatorComponent],
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
  readonly attention = computed(() => this.items().filter(item =>
    item.state === 'missing' || item.state === 'expiring' || item.state === 'unexpected').length);

  readonly operationsOpen = signal(false);
  readonly selectedCaller = signal('all');
  readonly operations = computed(() => this.service.inventory()?.gitHubOperations);
  readonly callerRows = computed(() => {
    const manifest = this.operations();
    const caller = this.selectedCaller();
    return (manifest?.callers ?? []).filter(row => caller === 'all' || row.id === caller).map(row => ({
      ...row,
      receipts: manifest?.receipts.filter(receipt => receipt.caller === row.id) ?? [],
      writes: manifest?.writes.filter(write => write.caller === row.id) ?? [],
      serviceState: manifest?.serviceStates?.[row.id],
    }));
  });
  readonly grantDependencies = computed(() => githubGrantDependencies(this.operations()?.receipts ?? []));
  readonly grantLabel = githubGrantLabel;

  targetLabel(target: GitHubOperationTarget | null | undefined): string {
    return target ? [target.transport, target.repository, ...(this.operations()?.appCoverage ?? []).map(broker => {
      const coverage = broker.repositories.find(row => row.repository === target.repository);
      return broker.broker + ' App ' + (coverage?.state ?? (broker.connected ? 'coverage unverified' : 'disconnected'))
        + (coverage?.installationId ? ' (' + coverage.installationId + ')' : '');
    })].join(' · ') : 'Unreported';
  }

  readonly hasAttempts = computed(() => this.service.inventory()?.items.some(item => item.binding) ?? false);

  constructor() { this.service.load(); this.providerAuth.start(); }

  openActions(item: CredentialInventoryItem): void {
    this.overlays.select(this.action(item).section);
  }

  /** Where this credential is managed. */
  action(item: CredentialInventoryItem): { section: 'github' | 'secrets' | 'access' | 'task-server' | 'remote-hosts'; label: string } {
    if (item.kind.startsWith('github_app') || item.kind === 'host_key') return { section: 'github', label: 'Open GitHub' };
    if (item.kind === 'service_account_token' || item.kind === 'operator_session') return { section: 'access', label: 'Open Access' };
    if (item.kind === 'secret' || item.kind === 'external_store_token') return { section: 'secrets', label: 'Open Secrets' };
    return item.category === 'service' || item.category === 'human'
      ? { section: 'task-server', label: 'Open Task Server actions' }
      : { section: 'remote-hosts', label: 'Open Execution Hosts' };
  }

  stateTone(state: CredentialState): StatusTone {
    return credentialStates[state]?.tone ?? 'neutral';
  }

  stateLabel(state: CredentialState): string {
    return credentialStates[state]?.label ?? state;
  }

  loginTone(state: string): StatusTone {
    return loginTones[state] ?? 'neutral';
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

  codexBadges(item: CredentialInventoryItem): ProviderAuthBadge[] {
    return this.providerAuth.statuses().filter(status =>
      status.provider === 'codex' && status.hostId === item.hostId);
  }

  signIn(badge: ProviderAuthBadge): void {
    this.codexSignIn.open({ hostId: badge.hostId, runnerId: badge.runnerId,
      hostName: badge.hostName, aliases: badge.aliases,
      baselineAdvertisedAt: badge.advertisedAt });
  }
}
