import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { AuthSessionState, type AuthStatus } from '../../../services/auth.service';
import { SecretsService, type SecretMetadata, type SecretOverview } from '../secrets.service';
import { SecretsPanelComponent } from './secrets-panel.component';

function secret(scope: string, name: string, status: 'active' | 'revoked' = 'active'): SecretMetadata {
  return {
    reference: `secret://${scope}/${name}`, scope, scopeKind: scope === 'workspace' ? 'workspace' : 'project', name, status,
    currentVersion: status === 'active' ? 2 : null, createdAt: '2026-10-19T09:00:00Z', createdBy: 'human:first.owner',
    updatedAt: '2026-10-19T09:00:00Z', updatedBy: 'human:first.owner', rotatedAt: '2026-10-19T10:00:00Z',
    rotatedBy: 'human:first.owner', lastUsedAt: null, lastUsedBy: null, lastUsedPurpose: null,
    versions: [
      { version: 2, origin: 'rotate', active: status === 'active', createdAt: '2026-10-19T10:00:00Z', createdBy: 'human:first.owner', revokedAt: null, revokedBy: null, lastUsedAt: null },
      { version: 1, origin: 'put', active: false, createdAt: '2026-10-19T09:00:00Z', createdBy: 'human:first.owner', revokedAt: '2026-10-19T11:00:00Z', revokedBy: 'human:first.owner', lastUsedAt: null },
    ],
  };
}

function setup(role: 'owner' | 'viewer' = 'owner') {
  const overview = signal<SecretOverview | null>({
    store: 'builtin', keySource: 'key-file', canWriteWorkspace: true,
    secrets: [secret('workspace', 'MAIL_KEY'), secret('memowikis', 'DATABASE_URL'), secret('memowikis/stage', 'DATABASE_URL', 'revoked')],
    unreadable: [],
  });
  const service = {
    overview, state: signal('ready'), error: signal<string | null>(null), load: vi.fn(),
    environments: vi.fn(() => of(['stage', 'prod'])),
    add: vi.fn(() => of(secret('workspace', 'NEW_KEY'))),
    rotate: vi.fn(() => of(secret('workspace', 'MAIL_KEY'))),
    revoke: vi.fn(() => of(secret('workspace', 'MAIL_KEY'))),
  };
  const auth = { status: signal<AuthStatus | null>({ profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'u', username: 'u', displayName: 'U', role, projects: [], disabled: false, mustChangePassword: false } as never }) };
  TestBed.configureTestingModule({
    imports: [SecretsPanelComponent],
    providers: [provideZonelessChangeDetection(),
      { provide: SecretsService, useValue: service }, { provide: AuthSessionState, useValue: auth }],
  });
  const fixture = TestBed.createComponent(SecretsPanelComponent);
  fixture.componentRef.setInput('projects', [{ name: 'memowikis', path: '/p', rootPath: '/p' }]);
  fixture.detectChanges();
  return { fixture, service, element: fixture.nativeElement as HTMLElement };
}

describe('SecretsPanelComponent', () => {
  it('lists the secrets of the selected scope with versions and metadata', () => {
    const { fixture, service, element } = setup();
    expect(element.querySelectorAll('[data-testid="secrets-row"]')).toHaveLength(1);
    expect(element.textContent).toContain('secret://workspace/MAIL_KEY');

    fixture.componentInstance.selectProject('memowikis');
    fixture.detectChanges();
    expect(service.environments).toHaveBeenCalledWith('memowikis');
    expect(element.textContent).toContain('secret://memowikis/DATABASE_URL');
    fixture.componentInstance.environment.set('stage');
    fixture.detectChanges();
    const row = element.querySelector('[data-testid="secrets-row"]')!;
    expect(row.getAttribute('data-status')).toBe('revoked');
    expect(row.querySelectorAll('.secrets__versions li')).toHaveLength(2);
  });

  it('adds a secret through a password field and clears the value after saving', () => {
    const { fixture, service, element } = setup();
    const value = `value-${crypto.randomUUID()}`;
    fixture.componentInstance.newName.set('NEW_KEY');
    fixture.componentInstance.newValue.set(value);
    fixture.detectChanges();
    expect((element.querySelector('[data-testid="secrets-add-value"]') as HTMLInputElement).type).toBe('password');
    fixture.componentInstance.add();
    expect(service.add).toHaveBeenCalledWith('workspace', 'NEW_KEY', value);
    expect(fixture.componentInstance.newValue()).toBe('');
    fixture.detectChanges();
    expect(element.innerHTML).not.toContain(value);
  });

  it('rotates and revokes in the selected scope and warns before a replacing add', () => {
    const { fixture, service, element } = setup();
    fixture.componentInstance.newName.set('MAIL_KEY');
    fixture.detectChanges();
    expect(element.textContent).toContain('Replacing revokes every earlier version');
    fixture.componentInstance.startRotate('MAIL_KEY');
    fixture.componentInstance.rotateValue.set('next');
    fixture.componentInstance.rotate('MAIL_KEY');
    expect(service.rotate).toHaveBeenCalledWith('workspace', 'MAIL_KEY', 'next');
    expect(fixture.componentInstance.rotating()).toBeNull();
    fixture.componentInstance.revoke('MAIL_KEY', 2);
    expect(service.revoke).toHaveBeenCalledWith('workspace', 'MAIL_KEY', 2);
  });

  it('warns about stored items the configured key cannot open while still listing the rest', () => {
    const { fixture, service, element } = setup();
    expect(element.querySelector('[data-testid="secrets-unreadable"]')).toBeNull();
    service.overview.update(current => current && { ...current, unreadable: [
      { id: 'a1', scope: 'memowikis', name: 'API_TOKEN', problem: 'key-unavailable', reason: 'other key' },
      { id: 'b2', scope: null, name: null, problem: 'integrity-failed', reason: 'mac' },
    ] });
    fixture.detectChanges();
    const warning = element.querySelector('[data-testid="secrets-unreadable"]')!;
    expect(warning.textContent).toContain('2 stored secrets cannot be opened');
    expect(warning.textContent).toContain('secret://memowikis/API_TOKEN');
    expect(warning.textContent).toContain('b2');
    expect(element.querySelectorAll('[data-testid="secrets-row"]')).toHaveLength(1);
  });

  it('shows metadata without write controls to a viewer', () => {
    const { element } = setup('viewer');
    expect(element.querySelector('[data-testid="secrets-add"]')).toBeNull();
    expect(element.querySelector('[data-testid="secrets-rotate"]')).toBeNull();
    expect(element.querySelectorAll('[data-testid="secrets-row"]')).toHaveLength(1);
  });
});
