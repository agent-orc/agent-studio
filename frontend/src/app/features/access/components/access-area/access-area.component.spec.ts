import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { AccessService } from '../../services/access.service';
import { AccessSelectionService } from '../../services/access-selection.service';
import { BackupActivityService } from '../../services/backup-activity.service';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import { OperatorApprovalsService } from '../../services/operator-approvals.service';
import { StandingGrantsService } from '../../services/standing-grants.service';
import { accessOverview, fakeAccessService } from '../../testing/access-fixtures';
import { fakeOperatorApprovalsService } from '../../testing/approval-fixtures';
import { fakeBackupActivityService } from '../../testing/backup-fixtures';
import { fakeStandingGrantsService } from '../../testing/grant-fixtures';
import { fakeOperatorAgentsService } from '../../testing/operator-fixtures';
import { AccessAreaComponent } from './access-area.component';
import { AuthSessionState, type AuthStatus } from '../../../../services/auth.service';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

describe('AccessAreaComponent', () => {
  async function render(access: ReturnType<typeof fakeAccessService>, operators = fakeOperatorAgentsService(),
    grants = fakeStandingGrantsService(), approvals = fakeOperatorApprovalsService(), backups = fakeBackupActivityService(),
    auth?: AuthStatus) {
    await TestBed.configureTestingModule({
      imports: [AccessAreaComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: AccessService, useValue: access },
        { provide: OperatorAgentsService, useValue: operators },
        { provide: StandingGrantsService, useValue: grants },
        { provide: OperatorApprovalsService, useValue: approvals },
        { provide: BackupActivityService, useValue: backups },
        provideHttpClient(), provideHttpClientTesting(),
      ],
    }).compileComponents();
    if (auth) TestBed.inject(AuthSessionState).status.set(auth);
    const fixture = TestBed.createComponent(AccessAreaComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixtureRef = fixture;
    return fixture.nativeElement as HTMLElement;
  }
  let fixtureRef: ReturnType<typeof TestBed.createComponent<AccessAreaComponent>>;
  /** The page provides its own selection; tests reach it through the component. */
  const selection = () => fixtureRef.debugElement.injector.get(AccessSelectionService);
  const settle = async () => {
    fixtureRef.detectChanges();
    await fixtureRef.whenStable();
    fixtureRef.detectChanges();
  };
  const outline = (element: HTMLElement) => [...element.querySelectorAll('[data-testid^="access-outline-"]')].map(node => node.textContent!.replace(/\s+/g, ' ').trim());

  it('loads once and shows every section with its grid, in one outline with counts', async () => {
    const access = fakeAccessService();
    const element = await render(access);
    expect(access.load).toHaveBeenCalledTimes(1);
    expect(outline(element)).toEqual([
      'People 2', 'Groups 1', 'Service accounts 0', 'API tokens 0', 'Runners 0', 'Role bindings 3',
      'Session limits', 'Elevated sessions', 'Operator agents', 'Approval requests', 'Standing grants', 'Kill switch', 'Break-glass windows', 'Operator activity',
      'Factor activity 0', 'Backup activity',
    ]);
    const developer = element.querySelector('[data-testid="access-user-dev.user"]')!;
    expect([...developer.querySelectorAll('.chip')].map(chip => chip.getAttribute('title')))
      .toEqual(['Developer · Website', 'Approver · Website · production']);
    expect(developer.textContent).toContain('Invited');
    expect(element.querySelector('[data-testid="access-single-user"]')).toBeNull();
    for (const section of ['access-users', 'access-bindings', 'access-groups', 'access-service-accounts-empty', 'access-api-tokens-empty', 'access-runners',
      'access-operator-agents', 'access-operator-approvals', 'access-standing-grants', 'access-operator-activity', 'access-backup-activity', 'factor-activity'])
      expect(element.querySelector(`[data-testid="${section}"]`), section).toBeTruthy();
    for (const anchor of outline(element).length ? fixtureRef.componentInstance.outline().map(item => item.id) : [])
      expect(element.querySelector(`#${anchor}`), anchor).toBeTruthy();
  });

  it('gives every section one sentence of purpose and at most one primary action', async () => {
    const element = await render(fakeAccessService());
    const sections = [...element.querySelectorAll('.access__sections > app-page-section')];
    expect(sections.length).toBeGreaterThanOrEqual(7);
    for (const section of sections) {
      expect(section.querySelector('.page-section__purpose')!.textContent!.length).toBeGreaterThan(20);
      expect(section.querySelectorAll(':scope > section > header .btn--primary').length).toBeLessThanOrEqual(1);
    }
    expect([...element.querySelectorAll('.access__sections > app-page-section > section > header .btn--primary')].map(button => button.textContent!.trim()))
      .toEqual(['Invite person', 'New group', 'New service account', 'New enrollment code', 'Add role binding']);
  });

  it('opens one detail area for the selected row and replaces it when another row is selected', async () => {
    const element = await render(fakeAccessService());
    expect(element.querySelector('[data-testid="access-detail-panel"]')).toBeNull();

    element.querySelector<HTMLButtonElement>('[data-testid="access-user-dev.user"] .grid__open')!.click();
    await settle();
    const panel = () => element.querySelector('[data-testid="access-detail-panel"]')!;
    expect(panel().getAttribute('aria-label')).toBe('Person: Dev User');
    expect(panel().querySelector('[data-testid="access-person-detail"]')).toBeTruthy();

    element.querySelector<HTMLButtonElement>('[data-testid="access-group-grp_web"] .grid__open')!.click();
    await settle();
    expect(element.querySelectorAll('[data-testid="access-detail-panel"]')).toHaveLength(1);
    expect(panel().getAttribute('aria-label')).toBe('Group: Team Website');
    expect(panel().querySelector('[data-testid="access-person-detail"]')).toBeNull();
    expect(panel().querySelector('[data-testid="access-group-detail"]')).toBeTruthy();

    panel().querySelector<HTMLButtonElement>('[data-testid="sidesheet-close"]')!.click();
    await settle();
    expect(element.querySelector('[data-testid="access-detail-panel"]')).toBeNull();
  });

  it('opens each creation form in the detail area from the primary action of its section', async () => {
    const element = await render(fakeAccessService());
    for (const [button, form, title] of [
      ['access-invite-open', 'access-invite-form', 'People: Invite a person'],
      ['access-group-open', 'access-group-form', 'Groups: New group'],
      ['access-service-account-open', 'access-account-form', 'Service accounts: New service account'],
      ['access-enrollment-open', 'access-enrollment-form', 'Runners: New enrollment code'],
      ['access-binding-open', 'access-binding-form', 'Role bindings: Add a role binding'],
    ]) {
      element.querySelector<HTMLButtonElement>(`[data-testid="${button}"]`)!.click();
      await settle();
      const panel = element.querySelector('[data-testid="access-detail-panel"]')!;
      expect(panel.getAttribute('aria-label')).toBe(title);
      expect(panel.querySelector(`[data-testid="${form}"]`), form).toBeTruthy();
    }
  });

  it('closes the detail when its row is gone', async () => {
    const access = fakeAccessService();
    const element = await render(access);
    selection().open('group', 'grp_web');
    await settle();
    expect(element.querySelector('[data-testid="access-group-detail"]')).toBeTruthy();

    access.overview.set(accessOverview({ groups: [] }));
    await settle();
    expect(element.querySelector('[data-testid="access-detail-panel"]')).toBeNull();
    expect(selection().selection()).toBeNull();
  });

  it('loads operator agents for the Owner and hides them when the server refuses', async () => {
    const operators = fakeOperatorAgentsService();
    operators.available.set(false);
    const element = await render(fakeAccessService(), operators);
    expect(operators.load).toHaveBeenCalledTimes(1);
    expect(element.querySelector('[data-testid="access-operator-agents"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-operator-activity"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-service-accounts-empty"]')).toBeTruthy();
    expect(outline(element)).not.toContain('Operator agents');
  });

  it('loads backup activity for the Owner and hides it while loading or when the server refuses', async () => {
    const backups = fakeBackupActivityService(null);
    const element = await render(fakeAccessService(), fakeOperatorAgentsService(), fakeStandingGrantsService(),
      fakeOperatorApprovalsService(), backups);
    expect(backups.load).toHaveBeenCalledTimes(1);
    expect(element.querySelector('[data-testid="access-backup-activity"]')).toBeNull();
    backups.state.set('loading');
    await Promise.resolve();
    expect(element.querySelector('[data-testid="access-backup-activity"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-runners"]')).toBeTruthy();
  });

  it('loads standing grants for the Owner and hides them when the server refuses', async () => {
    const grants = fakeStandingGrantsService();
    grants.state.set('denied');
    const element = await render(fakeAccessService(), fakeOperatorAgentsService(), grants);
    expect(grants.load).toHaveBeenCalledTimes(1);
    expect(element.querySelector('[data-testid="access-standing-grants"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-operator-agents"]')).toBeTruthy();
  });

  it('loads approval requests for the Owner and hides them when the server refuses', async () => {
    const approvals = fakeOperatorApprovalsService();
    approvals.state.set('denied');
    const element = await render(fakeAccessService(), fakeOperatorAgentsService(), fakeStandingGrantsService(), approvals);
    expect(approvals.load).toHaveBeenCalledTimes(1);
    expect(element.querySelector('[data-testid="access-operator-approvals"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-standing-grants"]')).toBeTruthy();
  });

  it('counts each section as the rows it shows: API tokens apart, operator agents left out', async () => {
    const created = '2026-10-20T09:00:00Z';
    const element = await render(fakeAccessService(accessOverview({
      serviceAccounts: [
        { id: 'sa_ci', name: 'ci-bot', createdAt: created, tokens: [] },
        { id: 'sa_off', name: 'old-bot', createdAt: created, disabledAt: created, tokens: [] },
        { id: 'sa_tok', name: 'cli token', createdAt: created, origin: 'api-token', tokens: [] },
        { id: 'sa_opa', name: 'studio-operator', createdAt: created, origin: 'operator-agent', tokens: [] },
      ],
    })));
    expect(outline(element)).toEqual(expect.arrayContaining(['Service accounts 2', 'API tokens 1']));
    expect(element.querySelectorAll('[data-testid="access-service-accounts"] tbody tr')).toHaveLength(2);
    expect(element.querySelectorAll('[data-testid="access-api-tokens"] tbody tr')).toHaveLength(1);
    expect(element.querySelector('#access-accounts .count-badge')!.textContent!.trim()).toBe('2');
  });

  it('reloads access and an open operator activity view together', async () => {
    const access = fakeAccessService();
    const operators = fakeOperatorAgentsService();
    const approvals = fakeOperatorApprovalsService();
    const element = await render(access, operators, fakeStandingGrantsService(), approvals);
    operators.loadActivity.mockClear();
    (element.querySelector('[data-testid="access-reload"]') as HTMLButtonElement).click();
    expect(access.load).toHaveBeenCalledTimes(2);
    expect(operators.load).toHaveBeenCalledTimes(2);
    expect(approvals.load).toHaveBeenCalledTimes(2);
    expect(operators.loadActivity).toHaveBeenCalledTimes(1);
  });

  it('lists factor activity in the standard grid with one time format', async () => {
    const element = await render(fakeAccessService());
    TestBed.inject(HttpTestingController).expectOne('/api/auth/audit/factors').flush([
      { sequence: 1, at: '2026-10-20T09:00:00Z', actor: 'human:usr_owner', session: 'session-1', operation: 'factor.bound', target: 'user:usr_owner' },
    ]);
    await settle();
    const row = element.querySelector('[data-testid="access-factor-events"] tbody tr')!;
    expect(row.textContent).toContain('factor.bound');
    expect(row.textContent).toContain('user:usr_owner');
    expect(row.textContent).toContain('20 Oct 2026, 09:00 UTC');
    expect(outline(element)).toContain('Factor activity 1');
  });

  it('resets a password once and disables a user in the detail without touching role bindings', async () => {
    const access = fakeAccessService();
    const element = await render(access);
    selection().open('user', 'usr_dev');
    await settle();
    element.querySelector<HTMLButtonElement>('[data-testid="access-user-reset-password"]')!.click();
    await settle();
    expect(access.resetPassword).toHaveBeenCalledWith('usr_dev');
    expect(element.querySelector('[data-testid="access-temporary-password"]')?.textContent).toContain('dev.user');

    element.querySelector<HTMLButtonElement>('[data-testid="access-user-toggle-dev.user"]')!.click();
    await settle();
    expect(element.querySelector('[data-testid="access-user-toggle-dev.user-panel"]')!.textContent).toContain('end immediately');
    expect(access.setUserDisabled).not.toHaveBeenCalled();
    element.querySelector<HTMLButtonElement>('[data-testid="access-user-toggle-dev.user-confirm"]')!.click();
    await settle();
    expect(access.setUserDisabled).toHaveBeenCalledWith('usr_dev', true);
    expect(access.removeBinding).not.toHaveBeenCalled();
  });

  it('tells a single owner that nothing needs to be set up', async () => {
    const single = accessOverview();
    const element = await render(fakeAccessService({
      ...single, users: [single.users[0]], groups: [], bindings: [single.bindings[0]], invitations: [],
    }));
    expect(element.querySelector('[data-testid="access-single-user"]')).toBeTruthy();
    expect(element.querySelector('[data-testid="access-user-first.owner"] .chip')!.getAttribute('title')).toBe('Owner · Workspace');
    expect(element.querySelector('[data-testid="access-groups-empty"]')!.textContent).toContain('No groups');
  });

  it('does not show a cached temporary password after access is denied and restored', async () => {
    const access = fakeAccessService();
    const element = await render(access);
    selection().open('user', 'usr_dev');
    await settle();
    element.querySelector<HTMLButtonElement>('[data-testid="access-user-reset-password"]')!.click();
    await settle();
    expect(element.querySelector('[data-testid="access-temporary-password"]')).not.toBeNull();

    access.overview.set(null);
    access.state.set('denied');
    await settle();
    access.overview.set(accessOverview());
    access.state.set('ready');
    await settle();
    expect(element.querySelector('[data-testid="access-temporary-password"]')).toBeNull();
    expect(element.querySelector('[data-testid="access-detail-panel"]')).toBeNull();
  });

  it('offers to create the owner in place when the local profile has no session, then reloads', async () => {
    const access = fakeAccessService(null);
    access.state.set('denied');
    access.error.set('Access settings, runner enrollments, and production approval need a signed-in Owner.');
    const element = await render(access, fakeOperatorAgentsService(), fakeStandingGrantsService(), fakeOperatorApprovalsService(),
      fakeBackupActivityService(), { profile: 'local', bootstrapRequired: true, authenticated: false });
    expect(access.load).toHaveBeenCalledTimes(1);
    const open = element.querySelector<HTMLButtonElement>('[data-testid="access-owner-sign-in"] [data-testid="local-owner-open"]');
    expect(open?.textContent).toContain('Create owner account');
    open!.click();
    TestBed.tick();
    for (const [id, value] of [['local-owner-username', 'local.owner'], ['local-owner-password', 'correct-horse-battery']]) {
      const input = element.querySelector<HTMLInputElement>(`[data-testid="${id}"]`)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    element.querySelector<HTMLButtonElement>('[data-testid="local-owner-submit"]')!.click();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/studio/auth/bootstrap').flush({
      profile: 'local', bootstrapRequired: false, authenticated: true,
      user: { id: 'usr_1', username: 'local.owner', displayName: 'local.owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
    });
    expect(access.load).toHaveBeenCalledTimes(2);
    TestBed.tick();
    expect(element.querySelector('[data-testid="access-owner-sign-in"]')).toBeNull();
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
