import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AccessService } from '../../services/access.service';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { fakeOperatorAgentsService, signedInOperatorAgent } from '../../testing/operator-fixtures';
import { AccessElevatedSessionsComponent } from './access-elevated-sessions.component';

describe('AccessElevatedSessionsComponent', () => {
  it.each([false, true])('ends a live session or acknowledges rate termination without step-up (rate ended: %s)', async rateEnded => {
    const operators = fakeOperatorAgentsService();
    const agent = signedInOperatorAgent();
    operators.agents.set([rateEnded ? { ...agent, sessions: agent.sessions.map(session => ({ ...session, state: 'expired' as const, classes: [], elevationRateEndedAt: '2026-11-27T09:00:00Z' })) } : agent]);
    await TestBed.configureTestingModule({ imports: [AccessElevatedSessionsComponent], providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      { provide: AccessService, useValue: fakeAccessService() }, { provide: OperatorAgentsService, useValue: operators },
    ] }).compileComponents();
    const fixture = TestBed.createComponent(AccessElevatedSessionsComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('opsagent@workstation');
    const action = rateEnded ? 'End elevation' : 'End session';
    const end = [...host.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent?.trim() === action)!;
    expect(end.disabled).toBe(false);
    end.click();
    const http = TestBed.inject(HttpTestingController);
    http.expectNone(request => request.url.includes('step-up'));
    http.expectOne(`/api/auth/iam/operator-agents/opa_term/sessions/ses_term/actions/${rateEnded ? 'end-elevation' : 'end'}`).flush(null);
    await fixture.whenStable();
    expect(operators.load).toHaveBeenCalled();
    fixture.destroy();
    http.verify();
  });
});
