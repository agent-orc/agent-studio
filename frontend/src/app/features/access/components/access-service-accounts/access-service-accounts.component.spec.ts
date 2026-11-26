import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { ServiceAccount } from '../../models/access.model';
import { ACCESS_CLOCK } from '../../services/access-clock';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { accessOverview, fakeAccessService } from '../../testing/access-fixtures';
import { AccessServiceAccountsComponent } from './access-service-accounts.component';

const created = '2026-10-20T09:00:00Z';
const token = (id: string, expiresAt: string, revokedAt: string | null = null) =>
  ({ id, name: `${id} token`, createdAt: created, expiresAt, lastUsedAt: null, revokedAt });
export const ACCOUNTS: ServiceAccount[] = [
  { id: 'sa_ci', name: 'ci-bot', description: 'Deploys stage', createdAt: created, tokens: [
    token('tok_1', '2027-01-18T09:00:00Z'), token('tok_2', '2026-12-01T09:00:00Z'), token('tok_gone', '2026-11-01T09:00:00Z', created),
  ] },
  { id: 'sa_old', name: 'old-bot', description: null, createdAt: created, disabledAt: created, tokens: [] },
  { id: 'sa_tok_build', name: 'build bot', description: 'Migrated API token.', createdAt: created, origin: 'api-token', createdBy: 'usr_owner',
    tokens: [token('tok_build', '2026-11-19T09:00:00Z')] },
  { id: 'sa_operator', name: 'release-operator', description: null, createdAt: created, origin: 'operator-agent', tokens: [] },
];
export const ACCOUNT_BINDINGS = [
  { id: 'rb_sa', principalKind: 'service-account' as const, principalId: 'sa_ci', role: 'developer',
    scope: { kind: 'environment' as const, project: 'PROJ-001', environment: 'stage' }, source: 'manual', createdAt: created },
  { id: 'rb_build', principalKind: 'service-account' as const, principalId: 'sa_tok_build', role: 'operator-agent',
    scope: { kind: 'workspace' as const }, source: 'migration', createdAt: created },
];

describe('AccessServiceAccountsComponent', () => {
  async function render(kind: 'service-account' | 'api-token', overview = accessOverview({ serviceAccounts: ACCOUNTS, bindings: ACCOUNT_BINDINGS })) {
    const access = fakeAccessService(overview);
    await TestBed.configureTestingModule({
      imports: [AccessServiceAccountsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        AccessSelectionService, { provide: AccessService, useValue: access },
        { provide: ACCESS_CLOCK, useValue: () => Date.parse('2026-10-20T09:00:00Z') }],
    }).compileComponents();
    const fixture = TestBed.createComponent(AccessServiceAccountsComponent);
    fixture.componentRef.setInput('kind', kind);
    fixture.detectChanges();
    await fixture.whenStable();
    return { access, fixture, element: fixture.nativeElement as HTMLElement, selection: TestBed.inject(AccessSelectionService) };
  }
  const meta = (element: HTMLElement, id: string) =>
    element.querySelector(`[data-testid="access-service-account-${id}"] td[data-role="meta"]`)!.textContent!.trim();

  it('lists service accounts with role, state and a token summary, without API tokens or operator agents', async () => {
    const { element } = await render('service-account');

    expect(element.querySelectorAll('tbody tr')).toHaveLength(2);
    const bot = element.querySelector('[data-testid="access-service-account-sa_ci"]')!;
    expect(bot.textContent).toContain('ci-bot');
    expect(bot.textContent).toContain('Deploys stage');
    expect(bot.querySelector('.chip')!.getAttribute('title')).toBe('Developer · Website · stage');
    expect(meta(element, 'sa_ci')).toBe('2 tokens, next expires 1 Dec 2026');
    expect(element.querySelector('[data-testid="access-service-account-status-sa_old"]')!.textContent!.trim()).toBe('Disabled');
    expect(meta(element, 'sa_old')).toBe('–');
    expect(element.querySelector('[data-testid="access-service-account-sa_tok_build"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-service-account-sa_operator"]')).toBeNull();
  });

  it('lists API tokens in their own grid with the creator, the role and the expiry', async () => {
    const { element } = await render('api-token');
    const row = element.querySelector('[data-testid="access-service-account-sa_tok_build"]')!;

    expect(element.querySelector('[data-testid="access-api-tokens"]')).toBeTruthy();
    expect(element.querySelectorAll('tbody tr')).toHaveLength(1);
    expect(row.textContent).toContain('Created by First Owner');
    expect(row.querySelector('.chip')!.getAttribute('title')).toBe('Operator agent · Workspace');
    expect(meta(element, 'sa_tok_build')).toBe('1 token, expires 19 Nov 2026');
  });

  it('opens the detail from the row, and leads Disable to the confirmation there', async () => {
    const { access, element, fixture, selection } = await render('service-account');
    element.querySelector<HTMLButtonElement>('[data-testid="access-service-account-sa_ci"] .grid__open')!.click();
    expect(selection.selection()).toEqual({ mode: 'detail', kind: 'service-account', id: 'sa_ci' });

    const rows = fixture.componentInstance.rows();
    fixture.componentInstance.act('disable', rows[0]);
    expect(selection.armed()).toBe('disable');
    expect(access.disableServiceAccount).not.toHaveBeenCalled();
    expect(rows[1].menu).toEqual([{ kind: 'row', id: 'open', label: 'Open details' }]);
  });

  it('says what belongs in each empty grid', async () => {
    const accounts = await render('service-account', accessOverview());
    expect(accounts.element.querySelector('[data-testid="access-service-accounts-empty"]')!.textContent).toContain('Scripts and deploy jobs');
    TestBed.resetTestingModule();
    const tokens = await render('api-token', accessOverview());
    expect(tokens.element.querySelector('[data-testid="access-api-tokens-empty"]')!.textContent).toContain('operator CLI');
  });
});
