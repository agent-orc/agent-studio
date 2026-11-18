import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { AccessService } from '../../../access';
import { ENROLLMENT_CODE, fakeAccessService } from '../../../access/testing/access-fixtures';
import { RunHostEnrollmentComponent } from './run-host-enrollment';

describe('RunHostEnrollmentComponent', () => {
  it('issues a run host code for the chosen projects and prints the host commands', async () => {
    const access = fakeAccessService();
    await TestBed.configureTestingModule({
      imports: [RunHostEnrollmentComponent],
      providers: [provideZonelessChangeDetection(), { provide: AccessService, useValue: access }],
    }).compileComponents();
    const fixture = TestBed.createComponent(RunHostEnrollmentComponent);
    const done = vi.fn();
    fixture.componentInstance.done.subscribe(done);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const create = element.querySelector('[data-testid="run-host-create"]') as HTMLButtonElement;
    expect(create.disabled).toBe(true);
    (element.querySelector('[data-testid="run-host-project-PROJ-002"]') as HTMLInputElement).click();
    fixture.detectChanges();
    create.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(access.createEnrollment).toHaveBeenCalledWith('run-host-1', 'deploy', ['PROJ-002']);
    const command = element.querySelector('[data-testid="run-host-command"]')?.textContent ?? '';
    expect(command).toContain(ENROLLMENT_CODE);
    expect(command).toContain('"$RUNNER_ID"');
    (element.querySelector('[data-testid="run-host-done"]') as HTMLButtonElement).click();
    expect(done).toHaveBeenCalled();
  });
});
