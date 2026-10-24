import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { CredentialInventoryComponent } from './credential-inventory.component';
import { CredentialInventoryService, type CredentialInventory } from '../credential-inventory.service';
import { ProviderAuthStatusService } from '../../remote-hosts/services/provider-auth-status.service';
import { CodexSignInDialogService } from '../../remote-hosts/services/codex-sign-in-dialog.service';

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
    const open = vi.fn();
    const statuses = signal([{
      provider: 'codex', hostId: 'coding-a', runnerId: 'coding-a', hostName: 'Coding host',
      aliases: ['coding-a'], state: 'limited', advertisedAt: '2026-10-16T09:00:00Z',
    }]);
    TestBed.configureTestingModule({
      imports: [CredentialInventoryComponent],
      providers: [provideZonelessChangeDetection(),
        { provide: ProviderAuthStatusService, useValue: { start: vi.fn(), statuses } },
        { provide: CodexSignInDialogService, useValue: { open } }, {
        provide: CredentialInventoryService,
        useValue: { inventory: signal(INVENTORY), state: signal('ready'), load: vi.fn() },
      }],
    });
    const fixture = TestBed.createComponent(CredentialInventoryComponent);
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelectorAll('.credentials__row')).toHaveLength(2);
    expect(element.textContent).toContain('Affected cards: AGT-1');
    expect(element.querySelector('[data-testid="access-codex-login-state"]')?.textContent)
      .toContain('rate limited');
    element.querySelector<HTMLButtonElement>('[data-testid="access-codex-sign-in"]')?.click();
    expect(open).toHaveBeenCalledWith(expect.objectContaining({ hostId: 'coding-a', runnerId: 'coding-a' }));
    statuses.set([...statuses(), { ...statuses()[0], runnerId: 'review-a', hostName: 'Review host' }]);
    fixture.detectChanges();
    const actions = element.querySelectorAll<HTMLButtonElement>('[data-testid="access-codex-sign-in"]');
    expect(actions).toHaveLength(2);
    actions[1].click();
    expect(open).toHaveBeenLastCalledWith(expect.objectContaining({ hostId: 'coding-a', runnerId: 'review-a' }));
    fixture.componentInstance.host.set('review-a');
    fixture.detectChanges();
    expect(element.querySelectorAll('.credentials__row')).toHaveLength(1);
    fixture.componentInstance.state.set('ok');
    fixture.detectChanges();
    expect(element.textContent).toContain('No credentials match these filters.');
  });
});
