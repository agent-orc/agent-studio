import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import { OperatorSessionLimitsService } from '../../services/operator-session-limits.service';
import { fakeOperatorAgentsService } from '../../testing/operator-fixtures';
import { AccessSessionLimitsComponent } from './access-session-limits.component';

describe('AccessSessionLimitsComponent', () => {
  it('shows the stored limits, keeps edits local and passes the fresh factor to save', async () => {
    const limits = { defaultLifetimeHours: 168, ownerLifetimeHours: null, adminLifetimeHours: 168, idleHours: 24 };
    const service = { limits: signal(limits), busy: signal(false), error: signal(null), load: vi.fn(), save: vi.fn(async () => true) };
    await TestBed.configureTestingModule({ imports: [AccessSessionLimitsComponent], providers: [provideZonelessChangeDetection(),
      { provide: OperatorSessionLimitsService, useValue: service }, { provide: OperatorAgentsService, useValue: fakeOperatorAgentsService() },
    ] }).compileComponents();
    const fixture = TestBed.createComponent(AccessSessionLimitsComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('7 days');
    expect([...host.querySelectorAll('select')].slice(0, 3).map(select => select.value)).toEqual(['168', '0', '168']);
    const idle = host.querySelector<HTMLInputElement>('input[type="number"]')!;
    idle.value = '12'; idle.dispatchEvent(new Event('input'));
    expect(service.save).not.toHaveBeenCalled();
    const factor = host.querySelector<HTMLInputElement>('input[type="password"]')!;
    factor.value = '123456'; factor.dispatchEvent(new Event('input'));
    [...host.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent?.trim() === 'Save session limits')!.click();
    await fixture.whenStable();
    expect(service.save).toHaveBeenCalledWith({ ...limits, idleHours: 12 }, '123456', undefined, null);
    expect(fixture.componentInstance.factor()).toBe('');
    fixture.destroy();
  });
});
