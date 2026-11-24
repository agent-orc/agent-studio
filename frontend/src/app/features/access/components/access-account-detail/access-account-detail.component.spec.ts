import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ACCESS_CLOCK } from '../../services/access-clock';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { SERVICE_ACCOUNT_TOKEN, accessOverview, fakeAccessService } from '../../testing/access-fixtures';
import { ACCOUNTS, ACCOUNT_BINDINGS } from '../access-service-accounts/access-service-accounts.component.spec';
import { AccessAccountDetailComponent } from './access-account-detail.component';

describe('AccessAccountDetailComponent', () => {
  async function render(accountId: string) {
    const access = fakeAccessService(accessOverview({ serviceAccounts: ACCOUNTS, bindings: ACCOUNT_BINDINGS }));
    await TestBed.configureTestingModule({
      imports: [AccessAccountDetailComponent],
      providers: [provideZonelessChangeDetection(), AccessSelectionService, { provide: AccessService, useValue: access },
        { provide: ACCESS_CLOCK, useValue: () => Date.parse('2026-10-20T09:00:00Z') }],
    }).compileComponents();
    const fixture = TestBed.createComponent(AccessAccountDetailComponent);
    fixture.componentRef.setInput('accountId', accountId);
    fixture.detectChanges();
    await fixture.whenStable();
    return { access, fixture, element: fixture.nativeElement as HTMLElement, selection: TestBed.inject(AccessSelectionService) };
  }
  const click = (element: HTMLElement, testId: string) => element.querySelector<HTMLButtonElement>(`[data-testid="${testId}"]`)!.click();

  it('shows the roles and the active tokens of a service account in one place', async () => {
    const { element } = await render('sa_ci');

    expect(element.querySelector('[data-testid="access-binding-rb_sa"]')!.textContent).toContain('Developer');
    const tokens = element.querySelector('[data-testid="access-token-list"]')!;
    expect(tokens.querySelectorAll('li')).toHaveLength(2);
    expect(tokens.textContent).toContain('tok_1 token');
    expect(tokens.textContent).toContain('Expires 18 Jan 2027, 09:00 UTC');
    expect(tokens.textContent).toContain('last used never');
    expect(tokens.textContent).not.toContain('tok_gone');
  });

  it('issues a token with the chosen lifetime and shows the secret once, with a copy control', async () => {
    const { access, element, fixture } = await render('sa_ci');
    const lifetime = element.querySelector<HTMLSelectElement>('[data-testid="access-token-lifetime"]')!;
    lifetime.value = '30';
    lifetime.dispatchEvent(new Event('change'));
    click(element, 'access-token-create-sa_ci');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(access.createToken).toHaveBeenCalledWith('sa_ci', 'ci-bot token', '2026-11-19T09:00:00.000Z');
    const issued = element.querySelector('[data-testid="access-token-issued"]')!;
    expect(issued.textContent).toContain('Shown once');
    expect(issued.textContent).toContain('New token for ci-bot');
    expect(issued.textContent).toContain('expires 18 Jan 2027, 09:00 UTC');
    expect(element.querySelector('[data-testid="access-token-issued-value"]')!.textContent).toBe(SERVICE_ACCOUNT_TOKEN);
    expect(element.querySelector('[data-testid="access-token-issued-copy"]')).toBeTruthy();

    click(element, 'access-token-issued-done');
    fixture.detectChanges();
    expect(element.textContent).not.toContain(SERVICE_ACCOUNT_TOKEN);
  });

  it('drops the shown secret when another account is opened or access is refused', async () => {
    const { access, element, fixture } = await render('sa_ci');
    click(element, 'access-token-create-sa_ci');
    await fixture.whenStable();
    fixture.detectChanges();
    expect(element.textContent).toContain(SERVICE_ACCOUNT_TOKEN);

    access.state.set('denied');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(element.textContent).not.toContain(SERVICE_ACCOUNT_TOKEN);

    access.state.set('ready');
    click(element, 'access-token-create-sa_ci');
    await fixture.whenStable();
    fixture.detectChanges();
    fixture.componentRef.setInput('accountId', 'sa_tok_build');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(element.textContent).not.toContain(SERVICE_ACCOUNT_TOKEN);
  });

  it('revokes a token only after the confirmation in place', async () => {
    const { access, element, fixture } = await render('sa_ci');
    click(element, 'access-token-revoke-tok_1');
    fixture.detectChanges();
    expect(access.revokeToken).not.toHaveBeenCalled();
    expect(element.querySelector('[data-testid="access-token-revoke-tok_1-panel"]')!.textContent).toContain('stops working at once');

    click(element, 'access-token-revoke-tok_1-confirm');
    await fixture.whenStable();
    expect(access.revokeToken).toHaveBeenCalledWith('sa_ci', 'tok_1');
  });

  it('disables the account only after the confirmation, also when the row menu asked for it', async () => {
    const { access, element, fixture, selection } = await render('sa_ci');
    selection.open('service-account', 'sa_ci', 'disable');
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="access-service-account-disable-sa_ci-panel"]')!.textContent).toContain('ci-bot and all its tokens stop working');
    expect(access.disableServiceAccount).not.toHaveBeenCalled();

    click(element, 'access-service-account-disable-sa_ci-confirm');
    await fixture.whenStable();
    expect(access.disableServiceAccount).toHaveBeenCalledWith('sa_ci');
  });

  it('shows a disabled account without token or disable controls', async () => {
    const { element } = await render('sa_old');
    expect(element.querySelector('[data-testid="access-service-account-status"]')!.textContent!.trim()).toBe('Disabled');
    expect(element.querySelector('[data-testid="access-token-create-sa_old"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-service-account-disable-sa_old"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-binding-add"]')).toBeNull();
  });

  it('shows an API token with its role description and creator, and offers no new token', async () => {
    const { element } = await render('sa_tok_build');

    const role = element.querySelector('[data-testid="access-api-token-role-sa_tok_build"]')!;
    expect(role.textContent).toContain('Role: Operator agent');
    expect(role.textContent).toContain('no secrets, production approval');
    expect(element.textContent).toContain('stops working if First Owner is disabled');
    expect(element.querySelector('[data-testid="access-token-create-sa_tok_build"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-token-revoke-tok_build"]')).toBeTruthy();
    expect(element.querySelector('[data-testid="access-service-account-disable-sa_tok_build"]')!.textContent).toContain('Disable API token');
  });
});
