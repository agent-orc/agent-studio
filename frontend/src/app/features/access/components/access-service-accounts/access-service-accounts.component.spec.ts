import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { ACCESS_CLOCK } from '../../services/access-clock';
import { AccessService } from '../../services/access.service';
import { SERVICE_ACCOUNT_TOKEN, accessOverview, fakeAccessService } from '../../testing/access-fixtures';
import { AccessServiceAccountsComponent } from './access-service-accounts.component';

describe('AccessServiceAccountsComponent', () => {
  let access: ReturnType<typeof fakeAccessService>;

  beforeEach(async () => {
    access = fakeAccessService(accessOverview({
      serviceAccounts: [
        { id: 'sa_ci', name: 'ci-bot', description: 'Pipelines', createdAt: '2026-10-20T09:00:00Z', tokens: [
          { id: 'tok_old', name: 'old', createdAt: '2026-10-20T09:00:00Z', expiresAt: '2027-01-18T09:00:00Z', lastUsedAt: null, revokedAt: null },
          { id: 'tok_gone', name: 'gone', createdAt: '2026-10-20T09:00:00Z', expiresAt: '2027-01-18T09:00:00Z', revokedAt: '2026-10-20T10:00:00Z' },
        ] },
        { id: 'sa_off', name: 'retired-bot', createdAt: '2026-10-20T09:00:00Z', disabledAt: '2026-10-20T11:00:00Z', tokens: [] },
        { id: 'sa_api', name: 'build bot', createdAt: '2026-10-20T09:00:00Z', origin: 'api-token', createdBy: 'usr_owner', tokens: [
          { id: 'tok_api', name: 'build bot', createdAt: '2026-10-20T09:00:00Z', expiresAt: '2026-11-19T09:00:00Z', lastUsedAt: null, revokedAt: null },
        ] },
      ],
      bindings: [...accessOverview().bindings,
        { id: 'rb_sa', principalKind: 'service-account', principalId: 'sa_ci', role: 'viewer', scope: { kind: 'workspace' }, source: 'manual', createdAt: '2026-10-20T09:00:00Z' }],
    }));
    await TestBed.configureTestingModule({
      imports: [AccessServiceAccountsComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: AccessService, useValue: access },
        { provide: ACCESS_CLOCK, useValue: () => Date.parse('2026-10-20T09:00:00Z') },
      ],
    }).compileComponents();
  });

  it('lists bindings and live tokens and marks disabled accounts', () => {
    const fixture = TestBed.createComponent(AccessServiceAccountsComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const ci = element.querySelector('[data-testid="access-service-account-sa_ci"]')?.textContent ?? '';
    expect(ci).toContain('Viewer · Workspace');
    expect(ci).toContain('old');
    expect(ci).toContain('Never');
    expect(ci).not.toContain('gone');
    expect(element.querySelector('[data-testid="access-service-account-sa_off"]')?.textContent).toContain('Disabled');
  });

  it('issues a token with the chosen lifetime and shows it once', async () => {
    const fixture = TestBed.createComponent(AccessServiceAccountsComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    (element.querySelector('[data-testid="access-token-create-sa_ci"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(access.createToken).toHaveBeenCalledWith('sa_ci', 'ci-bot token', '2027-01-18T09:00:00.000Z');
    expect(element.querySelector('[data-testid="access-token-issued"]')?.textContent).toContain(SERVICE_ACCOUNT_TOKEN);
  });

  it('lists API tokens as service accounts with their creator and no reissue', () => {
    const fixture = TestBed.createComponent(AccessServiceAccountsComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const api = element.querySelector('[data-testid="access-service-account-sa_api"]')?.textContent ?? '';
    expect(api).toContain('API token');
    expect(api).toContain('Created by First Owner');
    expect(api).toContain('Revoke');
    expect(element.querySelector('[data-testid="access-token-create-sa_api"]')).toBeNull();
  });

  it('creates an account with its initial binding', async () => {
    const fixture = TestBed.createComponent(AccessServiceAccountsComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const name = element.querySelector('[data-testid="access-service-account-name"]') as HTMLInputElement;
    name.value = 'deploy-bot';
    name.dispatchEvent(new Event('input'));
    const project = element.querySelector('[data-testid="access-grant-project"]') as HTMLSelectElement;
    project.value = 'PROJ-001';
    project.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    (element.querySelector('[data-testid="access-service-account-create"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(access.createServiceAccount).toHaveBeenCalledWith('deploy-bot', '', [{ role: 'admin', scope: { kind: 'project', project: 'PROJ-001' } }]);
  });
});
