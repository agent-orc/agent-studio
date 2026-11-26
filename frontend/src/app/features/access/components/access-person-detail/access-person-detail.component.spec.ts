import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { accessOverview, fakeAccessService } from '../../testing/access-fixtures';
import { AccessPersonDetailComponent } from './access-person-detail.component';

describe('AccessPersonDetailComponent', () => {
  async function render(userId: string, access = fakeAccessService()) {
    await TestBed.configureTestingModule({
      imports: [AccessPersonDetailComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), AccessSelectionService,
        { provide: AccessService, useValue: access }],
    }).compileComponents();
    const fixture = TestBed.createComponent(AccessPersonDetailComponent);
    fixture.componentRef.setInput('userId', userId);
    fixture.detectChanges();
    await fixture.whenStable();
    return { access, fixture, element: fixture.nativeElement as HTMLElement, selection: TestBed.inject(AccessSelectionService) };
  }
  const click = (element: HTMLElement, testId: string) => element.querySelector<HTMLButtonElement>(`[data-testid="${testId}"]`)!.click();

  it('shows state, second factor and the roles of the person', async () => {
    const { element } = await render('usr_dev');
    expect(element.querySelector('[data-testid="access-person-status"]')!.textContent!.trim()).toBe('Invited');
    expect(element.textContent).toContain('dev.user');
    expect(element.querySelector('[data-testid="access-binding-rb_dev"]')!.textContent).toContain('Developer');
  });

  it('shows a reset password once, in the marked place, until dismissed', async () => {
    const { access, element, fixture } = await render('usr_dev');
    click(element, 'access-user-reset-password');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(access.resetPassword).toHaveBeenCalledWith('usr_dev');
    const issued = element.querySelector('[data-testid="access-temporary-password"]')!;
    expect(issued.textContent).toContain('Shown once');
    expect(issued.textContent).toContain('Temporary password for dev.user');
    expect(element.querySelector('[data-testid="access-temporary-password-value"]')!.textContent).toBe('Tmp-one-time');

    click(element, 'access-temporary-password-done');
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="access-temporary-password"]')).toBeNull();
  });

  it('drops a shown temporary password when access is refused', async () => {
    const { access, element, fixture } = await render('usr_dev');
    click(element, 'access-user-reset-password');
    await fixture.whenStable();
    fixture.detectChanges();
    expect(element.textContent).toContain('Tmp-one-time');

    access.state.set('denied');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(element.textContent).not.toContain('Tmp-one-time');
  });

  it('disables a user only after the confirmation names the consequence', async () => {
    const { access, element, fixture } = await render('usr_dev');
    click(element, 'access-user-toggle-dev.user');
    fixture.detectChanges();

    expect(access.setUserDisabled).not.toHaveBeenCalled();
    expect(element.querySelector('[data-testid="access-user-toggle-dev.user-panel"]')!.textContent).toContain('Open sessions of Dev User end immediately');
    click(element, 'access-user-toggle-dev.user-cancel');
    fixture.detectChanges();
    expect(access.setUserDisabled).not.toHaveBeenCalled();

    click(element, 'access-user-toggle-dev.user');
    fixture.detectChanges();
    click(element, 'access-user-toggle-dev.user-confirm');
    await fixture.whenStable();
    expect(access.setUserDisabled).toHaveBeenCalledWith('usr_dev', true);
  });

  it('opens the confirmation when the row menu asked for Disable, and enables a disabled user without one', async () => {
    const overview = accessOverview();
    const access = fakeAccessService({ ...overview, users: [overview.users[0], { ...overview.users[1], disabled: true }] });
    const { element, fixture, selection } = await render('usr_owner', access);
    selection.open('user', 'usr_owner', 'disable');
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="access-user-toggle-first.owner-panel"]')).toBeTruthy();

    fixture.componentRef.setInput('userId', 'usr_dev');
    fixture.detectChanges();
    click(element, 'access-user-toggle-dev.user');
    await fixture.whenStable();
    expect(access.setUserDisabled).toHaveBeenCalledWith('usr_dev', false);
  });
});
