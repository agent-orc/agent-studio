import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { RemoteHost } from '../../models/remote-host.model';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';
import { RunnerSetupDialogComponent } from './runner-setup-dialog';

const HOST: RemoteHost = {
  id: 'agent-runner-01',
  name: 'agent-runner-01',
  role: 'remote',
  address: 'agent-runner',
  clientId: 'runner-client-01',
  status: 'offline',
  os: 'Ubuntu 24.04 LTS',
  lastHeartbeatAt: null,
  uptimeLabel: null,
  capabilities: ['linux', 'dotnet 10'],
  cliQuotas: [],
  stats: null,
};

describe('RunnerSetupDialogComponent', () => {
  it('polls a pending Review restart until host and real-request proof complete', async () => {
    await TestBed.configureTestingModule({
      imports: [RunnerSetupDialogComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(RunnerSetupDialogComponent);
    fixture.componentRef.setInput('host', HOST);
    fixture.detectChanges();
    const component = fixture.componentInstance;
    const wait = vi.spyOn(TestBed.inject(ProviderAuthStatusService), 'waitForRenewalCompletion')
      .mockReturnValue(of('complete'));
    component.providerAuthSecret.set('fixture-provider-auth-value');
    component.provisionProviderAuth();
    TestBed.inject(HttpTestingController).expectOne(
      '/api/v1/management/remote-hosts/provider-auth').flush({
        provider: 'claude', environmentVariable: 'CLAUDE_CODE_OAUTH_TOKEN',
        host: 'agent-runner', state: 'installed-awaiting-runner',
        detail: 'Review restart pending.', requestedAt: '2026-08-04T12:00:00Z',
        restartedServices: ['agent-runner.service'], processEnvironmentVerified: false,
        operationId: 'renewal_fixture',
      });
    expect(wait).toHaveBeenCalledWith('renewal_fixture', 75_000, 'agent-runner');
    expect(component.providerAuthPhase()).toBe('ok');
  });

  it('blocks loopback until tunnel mode and every required value are explicit', async () => {
    await TestBed.configureTestingModule({
      imports: [RunnerSetupDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunnerSetupDialogComponent);
    fixture.componentRef.setInput('host', HOST);
    fixture.componentRef.setInput('workspaces', [{ name: 'agent-taskboard', path: 'C:/projects/agent-taskboard' }]);
    fixture.detectChanges();

    const component = fixture.componentInstance;
    const el: HTMLElement = fixture.nativeElement;
    expect(component.sshTarget()).toBe('agent-runner');
    expect(component.clientId()).toBe('runner-client-01');
    expect(el.querySelector('[data-testid="runner-setup-blocked"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="visible-cli-task-card"]')).toBeNull();

    component.connectionMode.set('lan');
    component.gitRemote.set('git@github.com:example/agent-studio.git');
    component.gitPushRemote.set('git@github.com:example/agent-studio.git');
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="runner-setup-loopback-block"]')).toBeTruthy();

    component.setConnectionMode('tunnel');
    fixture.detectChanges();
    expect(component.taskServerUrl()).toBe('http://127.0.0.1:15031');
    expect(component.ready()).toBe(false);
    expect(el.querySelector('[data-testid="visible-cli-task-card"]')).toBeNull();

    const secret = 'sk-ant-oat01-provider-auth-fixture';
    component.providerAuthSecret.set(secret);
    component.provisionProviderAuth();
    const request = TestBed.inject(HttpTestingController).expectOne(
      '/api/v1/management/remote-hosts/provider-auth',
    );
    expect(request.request.body).toEqual({
      sshTarget: 'agent-runner',
      runnerId: 'agent-runner-01',
      environmentVariable: 'CLAUDE_CODE_OAUTH_TOKEN',
      secret,
    });
    request.flush({
      provider: 'claude',
      environmentVariable: 'CLAUDE_CODE_OAUTH_TOKEN',
      host: 'agent-runner',
      state: 'installed-awaiting-runner',
      detail: 'The protected EnvironmentFile was installed.',
      requestedAt: '2026-08-04T12:00:00Z',
      restartedServices: [],
      processEnvironmentVerified: false,
    });
    fixture.detectChanges();

    expect(component.providerAuthSecret()).toBe('');
    expect(component.ready()).toBe(true);
    expect(el.querySelector('[data-testid="runner-setup-loopback-block"]')).toBeNull();
    expect(el.querySelector('[data-testid="visible-cli-task-card"]')).toBeTruthy();
    expect(component.request().prompt).toContain('/etc/agent-runner/provider-auth.env');
    expect(component.request().prompt).not.toContain(secret);
  });

  it('keeps setup blocked while an active runner still owes a successful fresh probe', async () => {
    await TestBed.configureTestingModule({
      imports: [RunnerSetupDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunnerSetupDialogComponent);
    fixture.componentRef.setInput('host', HOST);
    fixture.detectChanges();
    const component = fixture.componentInstance;
    component.setConnectionMode('tunnel');
    component.gitRemote.set('git@github.com:example/agent-studio.git');
    component.gitPushRemote.set('git@github.com:example/agent-studio.git');
    component.providerAuthSecret.set('sk-ant-oat01-active-runner-fixture');
    component.provisionProviderAuth();

    TestBed.inject(HttpTestingController).expectOne(
      '/api/v1/management/remote-hosts/provider-auth',
    ).flush({
      provider: 'claude',
      environmentVariable: 'CLAUDE_CODE_OAUTH_TOKEN',
      host: 'agent-runner',
      state: 'awaiting-probe',
      detail: 'The daemon received the provider variable. Waiting for the runner probe.',
      requestedAt: '2026-08-04T12:00:00Z',
      restartedServices: ['agent-runner.service'],
      processEnvironmentVerified: true,
    });

    expect(component.providerAuthSecret()).toBe('');
    expect(component.providerAuthBootstrapReady()).toBe(false);
    expect(component.ready()).toBe(false);
  });
});
