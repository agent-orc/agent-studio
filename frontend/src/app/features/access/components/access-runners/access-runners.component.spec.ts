import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { ACCESS_CLOCK } from '../../services/access-clock';
import { AccessService } from '../../services/access.service';
import { ENROLLMENT_CODE, fakeAccessService } from '../../testing/access-fixtures';
import { AccessRunnersComponent } from './access-runners.component';

describe('AccessRunnersComponent', () => {
  let access: ReturnType<typeof fakeAccessService>;

  beforeEach(async () => {
    access = fakeAccessService();
    access.enrollments.set([
      { id: 'enr_open', name: 'build-host', scopes: [], createdAt: '2026-10-20T08:55:00Z', expiresAt: '2026-10-20T09:10:00Z', usedAt: null },
      { id: 'enr_old', name: 'old-host', scopes: [], createdAt: '2026-10-19T08:00:00Z', expiresAt: '2026-10-19T08:15:00Z', usedAt: null },
    ]);
    access.runners.set([
      { id: 'runner_1', name: 'coding-host', createdAt: '2026-10-19T08:00:00Z', revokedAt: null, credentials: [{ id: 'cred_1', scopes: [] }, { id: 'cred_0', scopes: [], revokedAt: '2026-10-19T09:00:00Z' }] },
    ]);
    await TestBed.configureTestingModule({
      imports: [AccessRunnersComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: AccessService, useValue: access },
        { provide: ACCESS_CLOCK, useValue: () => Date.parse('2026-10-20T09:00:00Z') },
      ],
    }).compileComponents();
  });

  it('lists open and expired enrollment codes against the injected clock and the enrolled runners', () => {
    const fixture = TestBed.createComponent(AccessRunnersComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const rows = Array.from(element.querySelectorAll('[data-testid="access-enrollments"] tbody tr')).map(row => row.textContent ?? '');
    expect(rows[0]).toContain('Open');
    expect(rows[0]).toContain('Revoke code');
    expect(rows[1]).toContain('Expired or revoked');
    expect(rows[1]).not.toContain('Revoke code');
    const runner = element.querySelector('[data-testid="access-runner-runner_1"]')?.textContent ?? '';
    expect(runner).toContain('coding-host');
    expect(runner).toContain('Active');
    const tones = Array.from(element.querySelectorAll<HTMLElement>('[data-testid="access-enrollment-status"]')).map(node => node.dataset['tone']);
    expect(tones).toEqual(['info', 'neutral']);
    expect(element.querySelector<HTMLElement>('[data-testid="access-runner-runner_1"] [data-testid="access-runner-status"]')?.dataset['tone']).toBe('success');
  });

  it('shows an error state when the runner lists could not be loaded', () => {
    access.runners.set([]);
    access.runnersError.set('Runner enrollments and runners could not be loaded.');
    const fixture = TestBed.createComponent(AccessRunnersComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="access-runners-error"]')?.textContent).toContain('could not be loaded');
  });

  it('creates an engine enrollment code and shows it once', async () => {
    const fixture = TestBed.createComponent(AccessRunnersComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const name = element.querySelector('[data-testid="access-enrollment-name"]') as HTMLInputElement;
    name.value = 'engine-host';
    name.dispatchEvent(new Event('input'));
    const kind = element.querySelector('select') as HTMLSelectElement;
    kind.value = 'orchestrator';
    kind.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    (element.querySelector('[data-testid="access-enrollment-create"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(access.createEnrollment).toHaveBeenCalledWith('engine-host', 'orchestrator');
    expect(element.querySelector('[data-testid="access-enrollment-code"]')?.textContent).toContain(ENROLLMENT_CODE);
  });

  it('binds a runner enrollment code to the chosen projects and warns that none binds nothing', async () => {
    const fixture = TestBed.createComponent(AccessRunnersComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="access-enrollment-no-project"]')?.textContent).toContain('claims and reads nothing');
    const name = element.querySelector('[data-testid="access-enrollment-name"]') as HTMLInputElement;
    name.value = 'build-host';
    name.dispatchEvent(new Event('input'));
    (element.querySelector('[data-testid="access-enrollment-project-PROJ-002"]') as HTMLInputElement).click();
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="access-enrollment-no-project"]')).toBeNull();
    (element.querySelector('[data-testid="access-enrollment-create"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(access.createEnrollment).toHaveBeenCalledWith('build-host', 'runner', ['PROJ-002']);
    expect(element.querySelector('[data-testid="access-enrollment-code"]')?.textContent).toContain('expires 30 days after enrollment');
  });

  it('issues a run host code bound to the chosen projects and prints the host commands with the id to use', async () => {
    const fixture = TestBed.createComponent(AccessRunnersComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const kind = element.querySelector('[data-testid="access-enrollment-kind"]') as HTMLSelectElement;
    expect(Array.from(kind.options).map(option => option.textContent?.trim())).toContain('Run host (deploy)');
    kind.value = 'deploy';
    kind.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="access-enrollment-projects"] legend')?.textContent).toContain('may deploy');
    const name = element.querySelector('[data-testid="access-enrollment-name"]') as HTMLInputElement;
    name.value = 'run-host-1';
    name.dispatchEvent(new Event('input'));
    (element.querySelector('[data-testid="access-enrollment-project-PROJ-002"]') as HTMLInputElement).click();
    fixture.detectChanges();
    (element.querySelector('[data-testid="access-enrollment-create"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(access.createEnrollment).toHaveBeenCalledWith('run-host-1', 'deploy', ['PROJ-002']);
    const command = element.querySelector('[data-testid="access-run-host-command"]')?.textContent ?? '';
    expect(command).toContain(`agent-host --enroll ${location.origin} ${ENROLLMENT_CODE}`);
    expect(command).toContain('RUNNER_HOSTNAME=%s');
    expect(command).toContain('"$RUNNER_ID"');
    expect(command).toContain('agent-runner-deploy.service');
    (Array.from(element.querySelectorAll('button')).find(button => button.textContent?.trim() === 'Done') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="access-run-host-command"]')).toBeNull();
  });

  it('shows bound projects, a pre-binding notice, and the credential end of each runner', () => {
    access.runners.set([
      { id: 'runner_bound', name: 'web-host', createdAt: '2026-10-19T08:00:00Z', projects: ['PROJ-001'],
        credentials: [{ id: 'cred_b', scopes: [], expiresAt: '2026-11-18T09:00:00Z' }] },
      { id: 'runner_soon', name: 'soon-host', createdAt: '2026-10-19T08:00:00Z', projects: [],
        credentials: [{ id: 'cred_s', scopes: [], expiresAt: '2026-10-25T09:00:00Z' }] },
      { id: 'runner_legacy', name: 'old-host', createdAt: '2026-10-01T08:00:00Z', projects: ['*'],
        projectsNotice: 'Enrolled before project binding: holds all projects until you choose its projects.',
        credentials: [{ id: 'cred_l', scopes: [], expiresAt: null, expiryNotice: 'No expiry: issued before the 30-day default. Rotate it to adopt the default.' }] },
    ]);
    const fixture = TestBed.createComponent(AccessRunnersComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const text = (id: string) => element.querySelector(`[data-testid="${id}"]`)?.textContent ?? '';
    expect(text('access-runner-projects-runner_bound')).toContain('Website');
    expect(text('access-runner-expiry-runner_bound')).toContain('Expires 18 Nov 2026');
    expect(text('access-runner-projects-runner_soon')).toContain('No project');
    expect(text('access-runner-expiry-runner_soon')).toContain('Expires in 5 days');
    expect((element.querySelector('[data-testid="access-runner-expiry-runner_soon"] [data-testid="status-indicator"]') as HTMLElement).dataset['tone']).toBe('warning');
    expect(text('access-runner-projects-runner_legacy')).toContain('All projects');
    expect(text('access-runner-projects-runner_legacy')).toContain('Before binding');
    expect(text('access-runner-expiry-runner_legacy')).toContain('No expiry');
    expect(text('access-runner-expiry-runner_legacy')).toContain('Rotate it to adopt the default');
  });

  it('edits the project set of an enrolled runner', async () => {
    access.runners.set([
      { id: 'runner_bound', name: 'web-host', createdAt: '2026-10-19T08:00:00Z', projects: ['PROJ-001'],
        credentials: [{ id: 'cred_b', scopes: [], expiresAt: '2026-11-18T09:00:00Z' }] },
    ]);
    const fixture = TestBed.createComponent(AccessRunnersComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    (element.querySelector('[data-testid="access-runner-edit-projects-runner_bound"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (element.querySelector('[data-testid="access-runner-project-runner_bound-PROJ-002"]') as HTMLInputElement).click();
    fixture.detectChanges();
    (element.querySelector('[data-testid="access-runner-save-projects-runner_bound"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(access.updateRunnerProjects).toHaveBeenCalledWith('runner_bound', ['PROJ-001', 'PROJ-002']);
    expect(element.querySelector('[data-testid="access-runner-project-editor-runner_bound"]')).toBeNull();
  });
});
