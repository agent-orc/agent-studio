import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import type { RemoteHost } from '../../remote-hosts/models/remote-host.model';
import { CredentialInventoryService, type CredentialInventory } from '../credential-inventory.service';
import { HostCredentialBadgeComponent } from './host-credential-badge.component';

describe('HostCredentialBadgeComponent', () => {
  it('counts only alerts belonging to the host or one of its aliases', () => {
    const inventory: CredentialInventory = { generatedAt: '2026-10-16T09:00:00Z', items: [
      { id: 'one', category: 'repository', hostId: 'host-a', kind: 'review_read_file',
        label: 'Read access', scopes: [], repository: null, permission: 'read', expiresAt: null,
        lastUseAt: null, state: 'missing', required: true, detail: 'Missing', affectedCards: [] },
      { id: 'two', category: 'repository', hostId: 'host-b', kind: 'github_deploy_key',
        label: 'Other host', scopes: [], repository: null, permission: 'write', expiresAt: null,
        lastUseAt: null, state: 'expiring', required: false, detail: 'Expiring', affectedCards: [] },
    ] };
    TestBed.configureTestingModule({
      imports: [HostCredentialBadgeComponent],
      providers: [provideZonelessChangeDetection(), {
        provide: CredentialInventoryService,
        useValue: { inventory: signal(inventory), state: signal('ready'), ensureLoaded: vi.fn() },
      }],
    });
    const fixture = TestBed.createComponent(HostCredentialBadgeComponent);
    fixture.componentRef.setInput('host', { id: 'runner-1', clientId: 'host-a', name: 'Runner 1' } as RemoteHost);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('1 credential alert');
  });
});
