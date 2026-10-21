import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { AccessService } from '../../services/access.service';
import { accessOverview, fakeAccessService } from '../../testing/access-fixtures';
import { AccessAreaComponent } from './access-area.component';

describe('AccessAreaComponent', () => {
  async function render(access: ReturnType<typeof fakeAccessService>) {
    await TestBed.configureTestingModule({
      imports: [AccessAreaComponent],
      providers: [provideZonelessChangeDetection(), { provide: AccessService, useValue: access }],
    }).compileComponents();
    const fixture = TestBed.createComponent(AccessAreaComponent);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('loads once and shows users with direct and group roles per scope', async () => {
    const access = fakeAccessService();
    const element = await render(access);
    expect(access.load).toHaveBeenCalledTimes(1);
    expect(element.querySelector('[data-testid="access-summary"]')?.textContent).toContain('3 role bindings');
    const developer = element.querySelector('[data-testid="access-user-dev.user"]')?.textContent ?? '';
    expect(developer).toContain('Developer · Website');
    expect(developer).toContain('Approver · Website · production (group)');
    expect(developer).toContain('Invited');
    expect(element.querySelector('[data-testid="access-single-user"]')).toBeNull();
    for (const section of ['access-users', 'access-bindings', 'access-groups', 'access-service-accounts', 'access-invite-form', 'access-runners'])
      expect(element.querySelector(`[data-testid="${section}"]`)).toBeTruthy();
  });

  it('resets a password once and disables a user without touching role bindings', async () => {
    const access = fakeAccessService();
    const element = await render(access);
    const row = element.querySelector('[data-testid="access-user-dev.user"]') as HTMLElement;
    (Array.from(row.querySelectorAll('button')).find(button => button.textContent?.includes('Reset password')) as HTMLButtonElement).click();
    await Promise.resolve();
    await Promise.resolve();
    TestBed.tick();
    expect(access.resetPassword).toHaveBeenCalledWith('usr_dev');
    expect(element.querySelector('[data-testid="access-temporary-password"]')?.textContent).toContain('dev.user');
    (element.querySelector('[data-testid="access-user-toggle-dev.user"]') as HTMLButtonElement).click();
    expect(access.setUserDisabled).toHaveBeenCalledWith('usr_dev', true);
  });

  it('tells a single owner that nothing needs to be set up', async () => {
    const single = accessOverview();
    const element = await render(fakeAccessService({
      ...single, users: [single.users[0]], groups: [], bindings: [single.bindings[0]], invitations: [],
    }));
    expect(element.querySelector('[data-testid="access-single-user"]')).toBeTruthy();
    expect(element.querySelector('[data-testid="access-user-first.owner"]')?.textContent).toContain('Owner · Workspace');
  });

  it('shows the error of a denied account instead of the sections', async () => {
    const access = fakeAccessService(null);
    access.state.set('denied');
    access.error.set('Your account cannot view access settings.');
    const element = await render(access);
    expect(element.querySelector('[data-testid="access-error"]')?.textContent).toContain('cannot view');
    expect(element.querySelector('[data-testid="access-users"]')).toBeNull();
  });

  it('forgets a one-time password after access is denied', async () => {
    const access = fakeAccessService();
    const element = await render(access);
    const row = element.querySelector('[data-testid="access-user-dev.user"]') as HTMLElement;
    (Array.from(row.querySelectorAll('button')).find(button => button.textContent?.includes('Reset password')) as HTMLButtonElement).click();
    await Promise.resolve();
    TestBed.tick();
    expect(element.querySelector('[data-testid="access-temporary-password"]')).toBeTruthy();

    access.state.set('denied');
    access.overview.set(null);
    TestBed.tick();
    access.state.set('ready');
    access.overview.set(accessOverview());
    TestBed.tick();
    expect(element.querySelector('[data-testid="access-temporary-password"]')).toBeNull();
  });
});
