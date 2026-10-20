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
});
