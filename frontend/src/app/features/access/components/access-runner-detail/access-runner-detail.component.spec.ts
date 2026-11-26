import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { ACCESS_CLOCK } from '../../services/access-clock';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { AccessRunnerDetailComponent } from './access-runner-detail.component';

describe('AccessRunnerDetailComponent', () => {
  let access: ReturnType<typeof fakeAccessService>;

  beforeEach(async () => {
    access = fakeAccessService();
    access.enrollments.set([
      { id: 'enr_open', name: 'build-host', scopes: [], createdAt: '2026-10-20T08:55:00Z', expiresAt: '2026-10-20T09:10:00Z', usedAt: null, projects: ['PROJ-001'] },
      { id: 'enr_used', name: 'used-host', scopes: [], createdAt: '2026-10-19T08:00:00Z', expiresAt: '2026-10-19T08:15:00Z', usedAt: '2026-10-19T08:05:00Z' },
    ]);
    access.runners.set([
      { id: 'runner_bound', name: 'web-host', createdAt: '2026-10-19T08:00:00Z', projects: ['PROJ-001'],
        credentials: [{ id: 'cred_b', scopes: [], expiresAt: '2026-11-18T09:00:00Z' }] },
      { id: 'runner_legacy', name: 'old-host', createdAt: '2026-10-01T08:00:00Z', projects: ['*'],
        projectsNotice: 'Enrolled before project binding: holds all projects until you choose its projects.',
        credentials: [{ id: 'cred_l', scopes: [], expiresAt: null, expiryNotice: 'No expiry: issued before the 30-day default. Rotate it to adopt the default.' }] },
      { id: 'engine_main', name: 'engine', createdAt: '2026-10-01T08:00:00Z', projects: [], credentials: [{ id: 'cred_e', scopes: [] }] },
    ]);
    await TestBed.configureTestingModule({
      imports: [AccessRunnerDetailComponent],
      providers: [provideZonelessChangeDetection(), AccessSelectionService, { provide: AccessService, useValue: access },
        { provide: ACCESS_CLOCK, useValue: () => Date.parse('2026-10-20T09:00:00Z') }],
    }).compileComponents();
  });

  async function mount(kind: 'runner' | 'enrollment', id: string) {
    const fixture = TestBed.createComponent(AccessRunnerDetailComponent);
    fixture.componentRef.setInput('kind', kind);
    fixture.componentRef.setInput('itemId', id);
    fixture.detectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const click = (testId: string) => {
      (element.querySelector(`[data-testid="${testId}"]`) as HTMLElement).click();
      fixture.detectChanges();
    };
    return { fixture, element, click, selection: TestBed.inject(AccessSelectionService) };
  }

  it('shows identity, projects and credential of a runner, with the notices in full', async () => {
    const { element } = await mount('runner', 'runner_legacy');
    expect(element.textContent).toContain('Coding and review');
    expect(element.textContent).toContain('runner_legacy');
    expect(element.querySelector('[data-testid="access-runner-detail-projects-runner_legacy"]')!.textContent).toBe('All projects');
    expect(element.textContent).toContain('holds all projects until you choose its projects');
    expect(element.textContent).toContain('No expiry');
    expect(element.textContent).toContain('Rotate it to adopt the default');
  });

  it('edits the project set of an enrolled runner', async () => {
    const { element, click, fixture } = await mount('runner', 'runner_bound');
    click('access-runner-edit-projects-runner_bound');
    click('access-runner-project-runner_bound-PROJ-002');
    click('access-runner-save-projects-runner_bound');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(access.updateRunnerProjects).toHaveBeenCalledWith('runner_bound', ['PROJ-001', 'PROJ-002']);
    expect(element.querySelector('[data-testid="access-runner-project-editor-runner_bound"]')).toBeNull();
  });

  it('starts a runner from before project binding with no project chosen', async () => {
    const { element, click } = await mount('runner', 'runner_legacy');
    click('access-runner-edit-projects-runner_legacy');
    expect(element.querySelector('[data-testid="access-runner-project-editor-runner_legacy"]')!.textContent).toContain('claims and reads nothing');
  });

  it('offers no project editing for the engine', async () => {
    const { element } = await mount('runner', 'engine_main');
    expect(element.textContent).toContain('Not project-bound');
    expect(element.querySelector('[data-testid="access-runner-edit-projects-engine_main"]')).toBeNull();
  });

  it('revokes a runner only after the confirmation in place', async () => {
    const { element, click, fixture } = await mount('runner', 'runner_bound');
    click('access-runner-revoke-runner_bound');
    expect(access.revokeRunner).not.toHaveBeenCalled();
    expect(element.querySelector('[data-testid="access-runner-revoke-runner_bound-panel"]')!.textContent).toContain('must enroll again with a new code');

    click('access-runner-revoke-runner_bound-confirm');
    await fixture.whenStable();
    expect(access.revokeRunner).toHaveBeenCalledWith('runner_bound');
  });

  it('revokes an open enrollment code after the confirmation and offers none for a used code', async () => {
    const open = await mount('enrollment', 'enr_open');
    expect(open.element.textContent).toContain('Website');
    open.click('access-enrollment-revoke-enr_open');
    open.click('access-enrollment-revoke-enr_open-confirm');
    await open.fixture.whenStable();
    expect(access.revokeEnrollment).toHaveBeenCalledWith('enr_open');

    const used = await mount('enrollment', 'enr_used');
    expect(used.element.textContent).toContain('Used');
    expect(used.element.querySelector('[data-testid="access-enrollment-revoke-enr_used"]')).toBeNull();
  });
});
