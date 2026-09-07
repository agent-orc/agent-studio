import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import type { RemoteHost } from '../../models/remote-host.model';
import { ProviderSignInDialogService } from '../../services/codex-sign-in-dialog.service';
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
  it('blocks loopback until tunnel mode and connection values are explicit', async () => {
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
    expect(component.ready()).toBe(true);
    expect(el.querySelector('[data-testid="visible-cli-task-card"]')).toBeTruthy();

    expect(component.ready()).toBe(true);
    expect(el.querySelector('[data-testid="runner-setup-loopback-block"]')).toBeNull();
    expect(el.querySelector('[data-testid="visible-cli-task-card"]')).toBeTruthy();
    expect(component.request().prompt).toContain('provider re-auth action in Execution Hosts');
    expect(component.request().prompt).not.toContain('/etc/agent-runner/provider-auth.env');
  });

  it('opens a host-owned Claude sign-in without collecting a credential', async () => {
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
    component.openProviderSignIn('claude');

    const dialog = TestBed.inject(ProviderSignInDialogService);
    expect(dialog.request()).toMatchObject({
      provider: 'claude',
      hostId: 'agent-runner-01',
      sshTarget: 'agent-runner',
    });
    expect(component.ready()).toBe(true);
  });
});
