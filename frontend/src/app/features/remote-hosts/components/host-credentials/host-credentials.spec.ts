import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { CredentialView, CredentialViewRow } from '../../models/credential-view.model';
import { credentialDueLabel } from '../../models/credential-view.model';
import type { RemoteHost } from '../../models/remote-host.model';
import { ClaudeSignInDialogService } from '../../services/claude-sign-in-dialog.service';
import { CodexSignInDialogService } from '../../services/codex-sign-in-dialog.service';
import { CredentialViewService } from '../../services/credential-view.service';
import { HostCredentialsComponent } from './host-credentials';

const NOW = Date.parse('2026-10-13T09:00:00Z');

const HOST = {
  id: 'runner-a', name: 'agent-runner-a', clientId: 'runner-a', capacityHostId: 'host-a',
  address: 'ssh://agent@runner-a', role: 'remote', status: 'online', capabilities: [], capabilityHealth: [],
} as unknown as RemoteHost;

function row(overrides: Partial<CredentialViewRow>): CredentialViewRow {
  return {
    installationId: 'installation', hostId: 'host-a', credentialId: 'claude-native', kind: 'claude_native_login',
    provider: 'claude', renewalMethod: 'R2', adapter: 'native-cli-store', effectiveSource: 'active', localRef: null,
    scopes: [], services: ['coding', 'review'], owner: 'runner-provider-owner', generation: 'generation-a',
    supersedes: null, lastVerifiedAt: null, lastRealSuccessAt: null, observedAt: '2026-10-13T08:59:00Z',
    status: 'healthy', statusDetail: null, nextRetryAt: null, serviceAvailability: 'available', evidenceQuality: 'ok',
    due: { knowledge: 'unknown', subject: null, dueAt: null, thresholdDays: null, overdue: false, expiryUnknown: true,
      reviewDueAt: null, unknownReason: 'issuer-unknown' },
    reminder: null, healthItem: null, runbookHref: 'docs/operations/setup/cli-relogin-runbook.md', evidenceRefs: [],
    actions: [],
    ...overrides,
  };
}

describe('HostCredentialsComponent', () => {
  const claude = { open: vi.fn() };
  const codex = { open: vi.fn() };
  let view: ReturnType<typeof signal<CredentialView | null>>;

  function mount(rows: CredentialViewRow[], role: CredentialView['role'] = 'operator') {
    view.set({ role, generatedAt: '2026-10-13T09:00:00Z', rows });
    const fixture = TestBed.createComponent(HostCredentialsComponent);
    fixture.componentRef.setInput('host', HOST);
    fixture.componentRef.setInput('now', NOW);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="remote-host-detail-toggle-credentials"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    claude.open.mockReset();
    codex.open.mockReset();
    view = signal<CredentialView | null>(null);
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [HostCredentialsComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: ClaudeSignInDialogService, useValue: claude },
        { provide: CodexSignInDialogService, useValue: codex },
        { provide: CredentialViewService, useValue: { view, state: signal('ready'), ensureLoaded: vi.fn(), load: vi.fn() } },
      ],
    });
  });

  it.each([
    ['R1', 'claude_oauth_token', 'claude', 'environment'],
    ['R2', 'claude_native_login', 'claude', 'native'],
  ])('confirmed invalid %s login opens the matching Claude flow for this host', (method, kind, provider, mode) => {
    const element = mount([row({
      credentialId: kind, kind, provider, renewalMethod: method, status: 'credential_invalid',
      actions: [{ kind: 'renew', label: 'Renew', primary: true, method }],
    })]);
    (element.querySelector(`[data-testid="host-credential-renew-${kind}"]`) as HTMLButtonElement).click();
    expect(claude.open).toHaveBeenCalledWith(expect.objectContaining({ hostId: 'host-a', sshTarget: 'ssh://agent@runner-a', mode }));
    expect(codex.open).not.toHaveBeenCalled();
    expect(element.querySelector(`[data-testid="host-credential-status-${kind}"]`)?.textContent).toContain('Login invalid');
  });

  it('confirmed invalid Codex login opens the Codex device flow', () => {
    const element = mount([row({
      credentialId: 'codex', kind: 'codex_chatgpt_login', provider: 'codex', renewalMethod: 'R3',
      status: 'credential_invalid', actions: [{ kind: 'renew', label: 'Sign in to Codex on this host', primary: true, method: 'R3' }],
    })]);
    (element.querySelector('[data-testid="host-credential-renew-codex"]') as HTMLButtonElement).click();
    expect(codex.open).toHaveBeenCalledWith(expect.objectContaining({ hostId: 'host-a' }));
    expect(claude.open).not.toHaveBeenCalled();
  });

  it('provider incident reads as a scheduled retry with no action', () => {
    const element = mount([row({ status: 'provider_incident', nextRetryAt: '2026-10-13T09:01:00Z', serviceAvailability: 'unavailable' })]);
    expect(element.querySelector('[data-testid="host-credential-status-claude-native"]')?.textContent)
      .toContain('Provider incident · retry scheduled');
    expect(element.querySelector('[data-testid="host-credential-service-claude-native"]')?.textContent).toContain('Service unavailable');
    expect(element.querySelector('[data-testid="host-credential-actions-claude-native"]')).toBeNull();
    expect(element.textContent).not.toContain('Sign in');
  });

  it('viewer gets health only, without controls or locator detail', () => {
    const element = mount([row({ status: 'credential_invalid', actions: [] })], 'viewer');
    expect(element.querySelector('[data-testid="host-credential-renew-claude-native"]')).toBeNull();
    (element.querySelector('[data-testid="host-credential-toggle-claude-native"]') as HTMLButtonElement).click();
    expect(element.textContent).not.toContain('Locator');
  });

  it('shows the generation-keyed reminder and keeps unknown expiry unknown', () => {
    const element = mount([
      row({ credentialId: 'token', kind: 'claude_oauth_token', renewalMethod: 'R1',
        due: { knowledge: 'known', subject: 'expiry', dueAt: '2026-10-19T09:00:00Z', thresholdDays: 7, overdue: false,
          expiryUnknown: false, reviewDueAt: null, unknownReason: null },
        reminder: { generation: 'generation-a', subject: 'expiry', dueAt: '2026-10-19T09:00:00Z', thresholdDays: 7,
          openedAt: '2026-10-05T09:00:00Z', updatedAt: '2026-10-12T09:00:00Z' } }),
      row({}),
    ]);
    expect(element.querySelector('[data-testid="host-credential-due-token"]')?.textContent).toContain('Expires in 6 days');
    expect(element.querySelector('[data-testid="host-credential-reminder-token"]')?.textContent).toContain('7-day reminder');
    expect(element.querySelector('[data-testid="host-credential-due-claude-native"]')?.textContent).toContain('Expiry unknown');
    expect(element.querySelector('[data-testid="host-credential-status-claude-native"]')?.textContent).toContain('Verified');
  });

  it('labels rotation, overdue and sub-day due dates', () => {
    const due = { knowledge: 'known' as const, subject: 'rotation' as const, dueAt: '2026-10-14T09:00:00Z', thresholdDays: 1 as const,
      overdue: false, expiryUnknown: false, reviewDueAt: null, unknownReason: null };
    expect(credentialDueLabel(due, NOW)).toBe('Rotation due in 1 day');
    expect(credentialDueLabel({ ...due, subject: 'expiry', dueAt: '2026-10-13T15:00:00Z' }, NOW)).toBe('Expires in 6 h');
    expect(credentialDueLabel({ ...due, subject: 'expiry', overdue: true }, NOW)).toBe('Expired');
    expect(credentialDueLabel({ ...due, knowledge: 'none', dueAt: null }, NOW)).toBe('No expiry');
  });
});
