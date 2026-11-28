import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuthService } from '../../../../services/auth.service';
import { OperatorAgentsService } from '../../../access/services/operator-agents.service';
import { fakeOperatorAgentsService, signedInOperatorAgent } from '../../../access/testing/operator-fixtures';
import { ElevationControlComponent } from './elevation-control';

describe('ElevationControlComponent', () => {
  afterEach(() => { TestBed.resetTestingModule(); vi.restoreAllMocks(); });

  it('keeps countdown ticks local and refreshes only for a visible Owner once a minute', async () => {
    const operators = fakeOperatorAgentsService([]);
    const status = signal({ user: { role: 'owner' } });
    await TestBed.configureTestingModule({ imports: [ElevationControlComponent], providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      { provide: AuthService, useValue: { status } },
      { provide: OperatorAgentsService, useValue: operators },
    ] }).compileComponents();
    const intervals = vi.spyOn(globalThis, 'setInterval');
    const fixture = TestBed.createComponent(ElevationControlComponent);
    fixture.detectChanges();
    expect(operators.load).toHaveBeenCalledTimes(1);
    const tick = intervals.mock.calls.find(call => call[1] === 15_000)![0] as () => void;
    const refresh = intervals.mock.calls.find(call => call[1] === 60_000)![0] as () => void;
    tick();
    expect(operators.load).toHaveBeenCalledTimes(1);
    expect(operators.refresh).not.toHaveBeenCalled();
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    refresh();
    expect(operators.refresh).toHaveBeenCalledTimes(1);
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden');
    refresh();
    expect(operators.refresh).toHaveBeenCalledTimes(1);
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    status.set({ user: { role: 'admin' } });
    refresh();
    expect(operators.refresh).toHaveBeenCalledTimes(1);
    const clear = vi.spyOn(globalThis, 'clearInterval');
    fixture.destroy();
    expect(clear).toHaveBeenCalled();
  });

  it('shows live elevation quietly and removes the header control when classes end', async () => {
    const agent = signedInOperatorAgent();
    const entry = (minutes: number) => ({ id: 'project.testing', confirmedBy: 'usr_owner', mode: 'direct-action' as const,
      armingId: 'arm_test', armedAt: new Date().toISOString(), armedUntil: new Date(Date.now() + minutes * 60_000).toISOString(), ratePerHour: 6, uses: [] });
    const session = agent.sessions[0];
    const operators = fakeOperatorAgentsService([{ ...agent, sessions: [
      { ...session, elevation: { classes: [entry(60)] } },
      { ...session, id: 'other_session', elevation: { classes: [entry(30)] } },
    ] }]);
    await TestBed.configureTestingModule({ imports: [ElevationControlComponent], providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      { provide: AuthService, useValue: { status: signal({ user: { role: 'owner' } }) } },
      { provide: OperatorAgentsService, useValue: operators },
    ] }).compileComponents();
    const fixture = TestBed.createComponent(ElevationControlComponent);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Elevated');
    expect(fixture.nativeElement.textContent).toContain('testing 1h');
    operators.agents.set(operators.agents().map(agent => ({ ...agent, sessions: agent.sessions.map(session => ({ ...session, classes: [] })) })));
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[data-testid="studio-elevation-trigger"]')).toBeNull();
    fixture.componentInstance.list();
    expect(operators.focusElevatedList()).toBe(true);
    fixture.destroy();
  });
});
