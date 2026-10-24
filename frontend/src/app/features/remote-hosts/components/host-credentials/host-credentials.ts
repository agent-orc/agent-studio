import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import { RemoteHostDetailSummaryComponent } from '../remote-host-detail-summary/remote-host-detail-summary';
import {
  credentialDocHref,
  credentialDueLabel,
  credentialDueTone,
  credentialEvidenceQualityLabel,
  credentialKindLabel,
  credentialLastVerifiedLabel,
  credentialRowsForHost,
  credentialScopeLabel,
  credentialServiceLabel,
  credentialSourceLabel,
  credentialStatusPresentation,
  credentialSummary,
  type CredentialViewAction,
  type CredentialViewRow,
} from '../../models/credential-view.model';
import type { RemoteHost } from '../../models/remote-host.model';
import { providerAuthBadgesForHost } from '../../models/provider-auth.model';
import { ClaudeSignInDialogService } from '../../services/claude-sign-in-dialog.service';
import { CodexSignInDialogService } from '../../services/codex-sign-in-dialog.service';
import { CredentialViewService } from '../../services/credential-view.service';

/**
 * Credentials section of one execution host (I8, docs/credentials-and-logins/index.html#surface).
 * Shows kind, effective source, scope, owner, last verification and expiry or rotation
 * knowledge, with login health and service availability kept apart. Actions come from
 * the Task Server for the caller's role; renewal reuses the existing host sign-in flows.
 * Secret values have no reveal, download or copy affordance.
 */
@Component({
  // eslint-disable-next-line @angular-eslint/component-selector -- The host card owns the native section and its detail styling.
  selector: 'section[appHostCredentials]',
  standalone: true,
  imports: [DisclosureMarkerComponent, RemoteHostDetailSummaryComponent],
  templateUrl: './host-credentials.html',
  styleUrl: './host-credentials.scss',
  host: { 'data-testid': 'remote-host-detail-section-credentials' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HostCredentialsComponent {
  private readonly claudeSignIn = inject(ClaudeSignInDialogService);
  private readonly codexSignIn = inject(CodexSignInDialogService);
  private readonly credentials = inject(CredentialViewService);

  readonly host = input.required<RemoteHost>();
  readonly now = input(Date.now());

  readonly expanded = signal(false);
  readonly unavailable = computed(() => this.credentials.state() === 'unavailable');
  readonly rows = computed(() => credentialRowsForHost(this.credentials.view(), this.host()));
  readonly summary = computed(() => credentialSummary(this.rows(), this.credentials.state()));
  private readonly openRows = signal<ReadonlySet<string>>(new Set());
  readonly presented = computed(() => this.rows().map(row => ({
    row,
    kind: credentialKindLabel(row.kind),
    source: credentialSourceLabel(row),
    scope: credentialScopeLabel(row),
    lastVerified: credentialLastVerifiedLabel(row),
    due: credentialDueLabel(row.due, this.now()),
    dueTone: credentialDueTone(row.due),
    status: credentialStatusPresentation(row),
    service: credentialServiceLabel(row.serviceAvailability),
    quality: credentialEvidenceQualityLabel(row.evidenceQuality),
    runbook: credentialDocHref(row.runbookHref),
    detailId: `host-credential-detail-${row.credentialId.replace(/[^a-zA-Z0-9_-]/g, '-')}`,
  })));

  constructor() {
    this.credentials.ensureLoaded();
  }

  toggleSection(): void {
    this.expanded.update(open => !open);
    if (this.expanded()) this.credentials.load();
  }

  isOpen(row: CredentialViewRow): boolean {
    return this.openRows().has(row.credentialId);
  }

  toggle(row: CredentialViewRow): void {
    this.openRows.update(open => {
      const next = new Set(open);
      if (!next.delete(row.credentialId)) next.add(row.credentialId);
      return next;
    });
  }

  actionHref(action: CredentialViewAction): string | null {
    return credentialDocHref(action.href);
  }

  renew(row: CredentialViewRow, action: CredentialViewAction): void {
    const host = this.host();
    const baseline = providerAuthBadgesForHost(host, this.now())
      .find(badge => badge.provider === row.provider)?.advertisedAt ?? null;
    const target = {
      hostId: row.hostId,
      runnerId: host.clientId || host.id,
      hostName: host.name,
      aliases: [host.id, host.clientId, host.capacityHostId ?? '', host.name].filter(Boolean),
      sshTarget: host.address,
      baselineAdvertisedAt: baseline,
    };
    if (action.method === 'R3') this.codexSignIn.open(target);
    else if (action.method === 'R2') this.claudeSignIn.open({ ...target, mode: 'native' });
    else if (action.method === 'R1') this.claudeSignIn.open({ ...target, mode: 'environment' });
  }

  formatTime(value: string | null): string {
    return value
      ? new Date(value).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false })
      : 'not recorded';
  }
}
