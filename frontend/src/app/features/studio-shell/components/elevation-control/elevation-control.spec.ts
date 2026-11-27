import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { AuthService } from '../../../../services/auth.service';
import { OperatorAgentsService } from '../../../access/services/operator-agents.service';
import { fakeOperatorAgentsService, signedInOperatorAgent } from '../../../access/testing/operator-fixtures';
import { ElevationControlComponent } from './elevation-control';

describe('ElevationControlComponent', () => {
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
