import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';
import { AddHostWizardComponent } from './add-host-wizard';

describe('AddHostWizardComponent', () => {
  it('provisions provider auth through the protected endpoint and clears the secret', async () => {
    await TestBed.configureTestingModule({
      imports: [AddHostWizardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(AddHostWizardComponent);
    const component = fixture.componentInstance;
    const secret = 'fixture-provider-auth-value';
    component.name.set('agent-runner-02');
    component.address.set('ssh://agent@runner-02');
    component.providerAuthSecret.set(secret);
    component.provisionProviderAuth();

    const request = TestBed.inject(HttpTestingController).expectOne(
      '/api/v1/management/remote-hosts/provider-auth',
    );
    expect(request.request.body).toEqual({
      sshTarget: 'agent@runner-02',
      runnerId: 'agent-runner-02',
      environmentVariable: 'CLAUDE_CODE_OAUTH_TOKEN',
      secret,
    });
    request.flush({
      provider: 'claude',
      environmentVariable: 'CLAUDE_CODE_OAUTH_TOKEN',
      host: 'agent@runner-02',
      state: 'installed-awaiting-runner',
      detail: 'The protected EnvironmentFile was installed.',
      requestedAt: '2026-08-04T12:00:00Z',
      restartedServices: [],
      processEnvironmentVerified: false,
    });
    fixture.detectChanges();

    expect(component.providerAuthSecret()).toBe('');
    expect(component.providerAuthPhase()).toBe('waiting');
    expect(component.claudeAuthed()).toBe(false);
    expect(fixture.nativeElement.textContent).not.toContain(secret);
  });
});

describe('AddHostWizardComponent renewal verification', () => {
  it('keeps an installed API key pending until the durable two-unit proof completes', async () => {
    await TestBed.configureTestingModule({
      imports: [AddHostWizardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(AddHostWizardComponent);
    const component = fixture.componentInstance;
    const proof = new Subject<'complete' | 'recovery-required'>();
    const wait = vi.spyOn(TestBed.inject(ProviderAuthStatusService), 'waitForRenewalCompletion')
      .mockReturnValue(proof.asObservable());
    component.name.set('agent-runner-02');
    component.address.set('ssh://agent@runner-02');
    component.providerAuthEnvironmentVariable.set('ANTHROPIC_API_KEY');
    component.providerAuthSecret.set('fixture-provider-key-value');
    component.provisionProviderAuth();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/management/remote-hosts/provider-auth')
      .flush({ operationId: 'renewal_fixture', processEnvironmentVerified: true,
        detail: 'Installed; awaiting proof.' });
    expect(wait).toHaveBeenCalledWith('renewal_fixture', 15 * 60_000);
    expect(component.providerAuthPhase()).toBe('waiting');
    expect(component.claudeAuthed()).toBe(false);
    proof.next('complete');
    expect(component.providerAuthPhase()).toBe('ok');
    expect(component.claudeAuthed()).toBe(true);
  });
});
