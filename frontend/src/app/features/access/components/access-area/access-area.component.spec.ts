import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { AccessService } from '../../services/access.service';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import { accessOverview, fakeAccessService } from '../../testing/access-fixtures';
import { fakeOperatorAgentsService } from '../../testing/operator-fixtures';
import { AccessAreaComponent } from './access-area.component';

describe('AccessAreaComponent', () => {
  async function render(access: ReturnType<typeof fakeAccessService>, operators = fakeOperatorAgentsService()) {
    await TestBed.configureTestingModule({
      imports: [AccessAreaComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: AccessService, useValue: access },
        { provide: OperatorAgentsService, useValue: operators },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(AccessAreaComponent);
    fixture.detectChanges();
    await fixture.whenStable();
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
    const invited = element.querySelector<HTMLElement>('[data-testid="access-user-status-dev.user"]')!;
    expect([invited.dataset['tone'], invited.dataset['icon'], invited.textContent?.trim()]).toEqual(['info', 'info', 'Invited']);
    expect(element.querySelector('[data-testid="access-single-user"]')).toBeNull();
    for (const section of ['access-users', 'access-bindings', 'access-groups', 'access-service-accounts', 'access-invite-form', 'access-runners',
      'access-operator-agents', 'access-operator-activity'])
      expect(element.querySelector(`[data-testid="${section}"]`)).toBeTruthy();
  });

  it('loads operator agents for the Owner and hides them when the server refuses', async () => {
    const operators = fakeOperatorAgentsService();
    operators.available.set(false);
    const element = await render(fakeAccessService(), operators);
    expect(operators.load).toHaveBeenCalledTimes(1);
    expect(element.querySelector('[data-testid="access-operator-agents"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-operator-activity"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-service-accounts"]')).toBeTruthy();
  });

  it('counts service accounts without the ones behind operator agents', async () => {
    const element = await render(fakeAccessService(accessOverview({
      serviceAccounts: [
        { id: 'sa_ci', name: 'ci-bot', createdAt: '2026-10-20T09:00:00Z', tokens: [] },
        { id: 'sa_opa', name: 'studio-operator', createdAt: '2026-10-20T09:00:00Z', origin: 'operator-agent', tokens: [] },
      ],
    })));
    expect(element.querySelector('[data-testid="access-summary"]')?.textContent).toContain('1 service account');
  });

  it('reloads access and an open operator activity view together', async () => {
    const access = fakeAccessService();
    const operators = fakeOperatorAgentsService();
    const element = await render(access, operators);
    operators.loadActivity.mockClear();
    (element.querySelector('[data-testid="access-reload"]') as HTMLButtonElement).click();
    expect(access.load).toHaveBeenCalledTimes(2);
    expect(operators.load).toHaveBeenCalledTimes(2);
    expect(operators.loadActivity).toHaveBeenCalledTimes(1);
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

  it('does not show a cached temporary password after access is denied and restored', async () => {
    const access = fakeAccessService();
    const element = await render(access);
    const row = element.querySelector('[data-testid="access-user-dev.user"]') as HTMLElement;
    (Array.from(row.querySelectorAll('button')).find(button => button.textContent?.includes('Reset password')) as HTMLButtonElement).click();
    await Promise.resolve();
    await Promise.resolve();
    TestBed.tick();
    expect(element.querySelector('[data-testid="access-temporary-password"]')).not.toBeNull();

    access.overview.set(null);
    access.state.set('denied');
    TestBed.tick();
    access.overview.set(accessOverview());
    access.state.set('ready');
    TestBed.tick();
    expect(element.querySelector('[data-testid="access-temporary-password"]')).toBeNull();
  });

  it('shows the error of a denied account instead of the sections', async () => {
    const access = fakeAccessService(null);
    access.state.set('denied');
    access.error.set('Your account cannot view access settings.');
    const element = await render(access);
    expect(element.querySelector('[data-testid="access-error"]')?.textContent).toContain('cannot view');
    expect(element.querySelector('[data-testid="access-users"]')).toBeNull();
  });
});
