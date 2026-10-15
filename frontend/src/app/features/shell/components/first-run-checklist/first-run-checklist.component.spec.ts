import { credentialFreeRepositoryUrl } from './first-run-checklist.component';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FirstRunChecklistComponent } from './first-run-checklist.component';
import { AuthService } from '../../../../services/auth.service';
import { TaskService } from '../../../../services/task.service';
import { WorkspaceManagerService } from '../../state/workspace-manager.service';
import { WorkspaceOverlaysService } from '../../state/workspace-overlays.service';
import { RemoteHostsService } from '../../../remote-hosts/services/remote-hosts.service';
import { ProviderAuthStatusService } from '../../../remote-hosts/services/provider-auth-status.service';
import { CodexSignInDialogService } from '../../../remote-hosts/services/codex-sign-in-dialog.service';
import { ClaudeSignInDialogService } from '../../../remote-hosts/services/claude-sign-in-dialog.service';
import type { RemoteHost } from '../../../remote-hosts/models/remote-host.model';
import type { ProviderAuthBadge } from '../../../remote-hosts/models/provider-auth.model';
import { of } from 'rxjs';

describe('first-run repository URL', () => {
  it('accepts a credential-free clone URL', () => {
    expect(credentialFreeRepositoryUrl('https://github.com/example/demo.git')).toBe(true);
    expect(credentialFreeRepositoryUrl('ssh://git@github.com/example/demo.git')).toBe(true);
    expect(credentialFreeRepositoryUrl('git@github.com:example/demo.git')).toBe(true);
  });

  it.each([
    'https://owner:secret@github.com/example/demo.git',
    'https://github.com/example/demo.git?token=secret',
    'https://github.com/example/demo.git#secret',
    'ssh://owner@github.com/example/demo.git',
    'git@github.com:example/../demo.git',
    'file:///tmp/demo',
    'https://github.com/',
  ])('rejects unsafe URL %s', url => {
    expect(credentialFreeRepositoryUrl(url)).toBe(false);
  });
});

describe('FirstRunChecklistComponent probes', () => {
  const host = signal<RemoteHost[]>([]);
  const badges = signal<ProviderAuthBadge[]>([]);
  let http: HttpTestingController;
  const readyBadge = (): ProviderAuthBadge => ({
    id: 'auth-a', provider: 'codex', providerLabel: 'Codex', runnerId: 'runner-a',
    hostId: 'runner-a', hostName: 'Runner A', aliases: ['runner-a'], state: 'ok',
    signal: 'healthy', detail: '', advertisedAt: new Date().toISOString(),
    lastSeenAt: new Date().toISOString(), consecutiveFailures: 0, reachable: true,
    expiresAt: null, expiresSoon: false, expiryLabel: null, limitedUntil: null,
    history: [],
  });

  beforeEach(() => {
    localStorage.removeItem('studio-first-run-proof');
    host.set([]);
    badges.set([]);
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(), provideHttpClientTesting(),
      { provide: AuthService, useValue: { status: signal({ authenticated: true, profile: 'local' }) } },
      { provide: TaskService, useValue: { getDetail: vi.fn(() => of({ info: { state: '2-ready' } })) } },
      { provide: WorkspaceManagerService, useValue: {} },
      { provide: WorkspaceOverlaysService, useValue: {} },
      { provide: RemoteHostsService, useValue: { hosts: host, reload: vi.fn() } },
      { provide: ProviderAuthStatusService, useValue: { statuses: badges, refresh: vi.fn() } },
      { provide: CodexSignInDialogService, useValue: {} },
      { provide: ClaudeSignInDialogService, useValue: {} },
    ] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('keeps owner, project, and Git pending when authority reads fail', () => {
    const fixture = TestBed.createComponent(FirstRunChecklistComponent);
    fixture.detectChanges();
    http.expectOne('/api/v1/installation').flush({}, { status: 503, statusText: 'Unavailable' });
    http.expectOne('/api/v1/projects').flush({}, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();
    const component = fixture.componentInstance;
    expect(component.ownerReady()).toBe(false);
    expect(component.repositoryReady()).toBe(false);
    expect(component.gitReady()).toBe(false);
    fixture.destroy();
  });

  it('requires an admitted fresh repository proof from a connected host', () => {
    host.set([{ id: 'runner-a', clientId: 'runner-a', status: 'online',
      lastHeartbeatAt: new Date().toISOString() } as RemoteHost]);
    badges.set([readyBadge()]);
    const fixture = TestBed.createComponent(FirstRunChecklistComponent);
    fixture.detectChanges();
    http.expectOne('/api/v1/installation').flush({ installationId: 'install-a', ownerBootstrapped: true });
    http.expectOne('/api/v1/projects').flush([{ id: 'PROJ-1', workspaceId: 'ws-a', displayName: 'Demo',
      shortCode: 'DM', archived: false, storageLocation: '/tmp/demo' }]);
    http.expectOne('/api/v1/projects/PROJ-1/repository').flush({ registration: {
      projectId: 'PROJ-1', repositoryUrl: 'https://github.com/example/demo.git', integrationRef: 'main',
      releaseRef: null, deliveryPolicy: 'reviewed-publication',
    }, probes: [{ runnerId: 'runner-a', admitted: true, verdict: 'admitted', detail: null,
      observedAt: new Date().toISOString() }] });
    const component = fixture.componentInstance;
    expect(component.ownerReady()).toBe(true);
    expect(component.providersReady()).toBe(true);
    expect(component.gitReady()).toBe(true);
    host.set([{ ...host()[0], status: 'offline' }]);
    expect(component.gitReady()).toBe(false);
    fixture.destroy();
  });

  it('creates a proof card through the networked Task Server project route', () => {
    host.set([{ id: 'runner-a', clientId: 'runner-a', status: 'online',
      lastHeartbeatAt: new Date().toISOString() } as RemoteHost]);
    badges.set([readyBadge()]);
    const fixture = TestBed.createComponent(FirstRunChecklistComponent);
    fixture.detectChanges();
    http.expectOne('/api/v1/installation').flush({ installationId: 'install-a', ownerBootstrapped: true });
    http.expectOne('/api/v1/projects').flush([{ projectId: 'PROJ-1', workspaceId: 'ws-a',
      name: 'Demo', taskKeyPrefix: 'DM' }]);
    http.expectOne('/api/v1/projects/PROJ-1/repository').flush({ registration: {
      projectId: 'PROJ-1', repositoryUrl: 'https://github.com/example/demo.git', integrationRef: 'main',
      releaseRef: null, deliveryPolicy: 'reviewed-publication',
    }, probes: [{ runnerId: 'runner-a', admitted: true, verdict: 'admitted', detail: null,
      observedAt: new Date().toISOString() }] });
    const component = fixture.componentInstance;
    component.demoRepositoryConfirmed.set(true);
    expect(component.canStartProof()).toBe(true);
    component.startProof();
    const request = http.expectOne('/api/v1/studio/tasks');
    expect(request.request.body.projectId).toBe('PROJ-1');
    request.flush({ taskId: 'proof-a' });
    http.expectOne('/api/v1/projects/PROJ-1/tasks/proof-a').flush({ taskId: 'proof-a', state: '2-ready' });
    http.expectOne('/api/v1/projects/PROJ-1/tasks/proof-a/attempts').flush([]);
    http.expectOne('/api/v1/integrations?taskId=proof-a').flush([]);
    expect(component.proof()?.id).toBe('proof-a');
    expect(component.proofRan()).toBe(false);
    expect(component.proofReviewed()).toBe(false);
    expect(component.proofIntegrated()).toBe(false);
    component.reload();
    http.expectOne('/api/v1/installation').flush({ installationId: 'install-a', ownerBootstrapped: true });
    http.expectOne('/api/v1/projects').flush([{ projectId: 'PROJ-1', workspaceId: 'ws-a',
      name: 'Demo', taskKeyPrefix: 'DM' }]);
    http.expectOne('/api/v1/projects/PROJ-1/repository').flush({ registration: {
      projectId: 'PROJ-1', repositoryUrl: 'https://github.com/example/demo.git', integrationRef: 'main',
      releaseRef: null, deliveryPolicy: 'reviewed-publication',
    }, probes: [] });
    http.expectOne('/api/v1/projects/PROJ-1/tasks/proof-a').flush({ taskId: 'proof-a', state: '6-completed' });
    http.expectOne('/api/v1/projects/PROJ-1/tasks/proof-a/attempts').flush([{
      run: { resultSha: 'example-sha', finishedAt: new Date().toISOString() },
    }]);
    http.expectOne('/api/v1/integrations?taskId=proof-a').flush([{
      reviewAttemptId: 'review-a', status: 'integrated', integratedSha: 'example-sha',
    }]);
    expect(component.proofRan()).toBe(true);
    expect(component.proofReviewed()).toBe(true);
    expect(component.proofIntegrated()).toBe(true);
    fixture.destroy();
  });
});
