import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import { ACCESS_CLOCK } from '../../services/access-clock';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { AccessRunnersComponent } from './access-runners.component';

describe('AccessRunnersComponent', () => {
  let access: ReturnType<typeof fakeAccessService>;

  beforeEach(async () => {
    access = fakeAccessService();
    access.enrollments.set([
      { id: 'enr_open', name: 'build-host', scopes: [], createdAt: '2026-10-20T08:55:00Z', expiresAt: '2026-10-20T09:10:00Z', usedAt: null, projects: ['PROJ-001'] },
      { id: 'enr_old', name: 'old-host', scopes: ['runner.deploy'], createdAt: '2026-10-19T08:00:00Z', expiresAt: '2026-10-19T08:15:00Z', usedAt: null },
    ]);
    access.runners.set([
      { id: 'runner_1', name: 'coding-host', createdAt: '2026-10-19T08:00:00Z', revokedAt: null, credentials: [{ id: 'cred_1', scopes: [] }, { id: 'cred_0', scopes: [], revokedAt: '2026-10-19T09:00:00Z' }] },
    ]);
    await TestBed.configureTestingModule({
      imports: [AccessRunnersComponent],
      providers: [
        provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([]), AccessSelectionService,
        { provide: AccessService, useValue: access },
        { provide: ACCESS_CLOCK, useValue: () => Date.parse('2026-10-20T09:00:00Z') },
      ],
    }).compileComponents();
  });

  function mount() {
    const fixture = TestBed.createComponent(AccessRunnersComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const text = (id: string) => element.querySelector(`[data-testid="${id}"]`)?.textContent ?? '';
    return { fixture, element, text, selection: TestBed.inject(AccessSelectionService) };
  }

  it('lists open and expired enrollment codes against the injected clock and the enrolled runners', () => {
    const { fixture, element, text } = mount();

    expect(text('access-enrollment-enr_open')).toContain('Open');
    expect(text('access-enrollment-enr_open')).toContain('Coding and review');
    expect(text('access-enrollment-enr_open')).toContain('Website');
    expect(text('access-enrollment-enr_old')).toContain('Expired or revoked');
    expect(text('access-enrollment-enr_old')).toContain('Run host');
    const tones = Array.from(element.querySelectorAll<HTMLElement>('[data-testid="access-enrollment-status"]')).map(node => node.dataset['tone']);
    expect(tones).toEqual(['info', 'neutral']);
    const codes = fixture.componentInstance.enrollments();
    expect(codes[0].menu.at(-1)).toEqual({ kind: 'row', id: 'revoke', label: 'Revoke…', danger: true });
    expect(codes[1].menu).toEqual([{ kind: 'row', id: 'open', label: 'Open details' }]);

    expect(text('access-runner-runner_1')).toContain('coding-host');
    expect(text('access-runner-runner_1')).toContain('runner_1');
    expect(element.querySelector<HTMLElement>('[data-testid="access-runner-runner_1"] [data-testid="access-runner-status"]')?.dataset['tone']).toBe('success');
  });

  it('shows an error state when the runner lists could not be loaded, and says what belongs in the empty grids', () => {
    access.runners.set([]);
    access.enrollments.set([]);
    access.runnersError.set('Runner enrollments and runners could not be loaded.');
    const { text } = mount();

    expect(text('access-runners-error')).toContain('could not be loaded');
    expect(text('access-enrolled-runners-empty')).toContain('once it has used an enrollment code');
    expect(text('access-enrollments-empty')).toContain('use it once on the host');
  });

  it('shows identity, bound projects, a pre-binding notice, and the credential end of each runner', () => {
    access.runners.set([
      { id: 'runner_bound', name: 'web-host', createdAt: '2026-10-19T08:00:00Z', projects: ['PROJ-001'],
        credentials: [{ id: 'cred_b', scopes: [], expiresAt: '2026-11-18T09:00:00Z' }] },
      { id: 'runner_soon', name: 'soon-host', createdAt: '2026-10-19T08:00:00Z', projects: [],
        credentials: [{ id: 'cred_s', scopes: ['runner.deploy'], expiresAt: '2026-10-25T09:00:00Z' }] },
      { id: 'runner_legacy', name: 'old-host', createdAt: '2026-10-01T08:00:00Z', projects: ['*'],
        projectsNotice: 'Enrolled before project binding: holds all projects until you choose its projects.',
        credentials: [{ id: 'cred_l', scopes: [], expiresAt: null, expiryNotice: 'No expiry: issued before the 30-day default. Rotate it to adopt the default.' }] },
      { id: 'engine_main', name: 'engine', createdAt: '2026-10-01T08:00:00Z', projects: [], credentials: [{ id: 'cred_e', scopes: [] }] },
      { id: 'runner_gone', name: 'gone-host', createdAt: '2026-10-01T08:00:00Z', revokedAt: '2026-10-10T08:00:00Z', projects: ['PROJ-002'], credentials: [] },
    ]);
    const { fixture, element, text } = mount();

    expect(text('access-runner-projects-runner_bound')).toContain('Coding and review');
    expect(text('access-runner-projects-runner_bound')).toContain('Website');
    expect(text('access-runner-expiry-runner_bound')).toContain('Expires 18 Nov 2026');
    expect(text('access-runner-projects-runner_soon')).toContain('Run host');
    expect(text('access-runner-projects-runner_soon')).toContain('No project');
    expect(text('access-runner-expiry-runner_soon')).toContain('Expires in 5 days');
    expect((element.querySelector('[data-testid="access-runner-expiry-runner_soon"] [data-testid="status-indicator"]') as HTMLElement).dataset['tone']).toBe('warning');
    expect(text('access-runner-projects-runner_legacy')).toContain('All projects');
    expect(text('access-runner-projects-runner_legacy')).toContain('Before binding');
    expect(text('access-runner-expiry-runner_legacy')).toContain('No expiry');
    expect(text('access-runner-projects-engine_main')).toContain('Orchestration engine');
    expect(text('access-runner-projects-engine_main')).toContain('Not project-bound');
    expect(text('access-runner-runner_gone')).toContain('Revoked');

    const rows = fixture.componentInstance.runners();
    expect(rows[0].menu.map(item => item.kind === 'row' ? item.id : '-')).toEqual(['open', 'edit', '-', 'revoke']);
    expect(rows[3].menu.map(item => item.kind === 'row' ? item.id : '-')).toEqual(['open', '-', 'revoke']);
    expect(rows[4].menu.map(item => item.kind === 'row' ? item.id : '-')).toEqual(['open']);
  });

  it('opens the detail from a row and leads Revoke to the confirmation there', () => {
    const { fixture, element, selection } = mount();
    element.querySelector<HTMLButtonElement>('[data-testid="access-runner-runner_1"] .grid__open')!.click();
    expect(selection.selection()).toEqual({ mode: 'detail', kind: 'runner', id: 'runner_1' });

    fixture.componentInstance.actEnrollment('revoke', fixture.componentInstance.enrollments()[0]);
    expect(selection.selection()).toEqual({ mode: 'detail', kind: 'enrollment', id: 'enr_open' });
    expect(selection.armed()).toBe('revoke');
    expect(access.revokeEnrollment).not.toHaveBeenCalled();
  });
});
