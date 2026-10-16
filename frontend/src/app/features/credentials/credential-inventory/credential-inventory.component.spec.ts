import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { CredentialInventoryComponent } from './credential-inventory.component';
import { CredentialInventoryService, type CredentialInventory } from '../credential-inventory.service';

const INVENTORY: CredentialInventory = {
  generatedAt: '2026-10-16T09:00:00Z',
  items: [
    { id: 'one', category: 'repository', hostId: 'review-a', kind: 'review_read_file',
      label: 'Private repository read access', scopes: [], repository: 'github.com/team/private',
      permission: 'read', expiresAt: null, lastUseAt: null, state: 'missing', required: true,
      detail: 'Add a read credential', affectedCards: ['AGT-1'] },
    { id: 'two', category: 'provider', hostId: 'coding-a', kind: 'codex_chatgpt_login',
      label: 'Codex account', scopes: [], repository: null, permission: null, expiresAt: null,
      lastUseAt: null, state: 'ok', required: false, detail: 'Verified', affectedCards: [] },
  ],
};

describe('CredentialInventoryComponent', () => {
  it('shows required gaps and filters by host and state', () => {
    TestBed.configureTestingModule({
      imports: [CredentialInventoryComponent],
      providers: [provideZonelessChangeDetection(), {
        provide: CredentialInventoryService,
        useValue: { inventory: signal(INVENTORY), state: signal('ready'), load: vi.fn() },
      }],
    });
    const fixture = TestBed.createComponent(CredentialInventoryComponent);
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelectorAll('.credentials__row')).toHaveLength(2);
    expect(element.textContent).toContain('Affected cards: AGT-1');
    fixture.componentInstance.host.set('review-a');
    fixture.detectChanges();
    expect(element.querySelectorAll('.credentials__row')).toHaveLength(1);
    fixture.componentInstance.state.set('ok');
    fixture.detectChanges();
    expect(element.textContent).toContain('No credentials match these filters.');
  });
});
