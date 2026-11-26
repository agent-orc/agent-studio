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
  it('renders device facts and ends a session with one request and no step-up', async () => {
    const operators = fakeOperatorAgentsService();
    operators.agents.set([signedInOperatorAgent()]);
    await TestBed.configureTestingModule({ imports: [AccessElevatedSessionsComponent], providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      { provide: AccessService, useValue: fakeAccessService() }, { provide: OperatorAgentsService, useValue: operators },
    ] }).compileComponents();
    const fixture = TestBed.createComponent(AccessElevatedSessionsComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('opsagent@workstation');
    const end = [...host.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent?.trim() === 'End session')!;
    end.click();
    const http = TestBed.inject(HttpTestingController);
    http.expectNone(request => request.url.includes('step-up'));
    http.expectOne('/api/auth/iam/operator-agents/opa_term/sessions/ses_term/actions/end').flush(null);
    await fixture.whenStable();
    expect(operators.load).toHaveBeenCalled();
    fixture.destroy();
    http.verify();
  });
});
