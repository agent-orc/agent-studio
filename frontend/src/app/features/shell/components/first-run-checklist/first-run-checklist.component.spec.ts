import { credentialFreeRepositoryUrl } from './first-run-checklist.models';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FirstRunChecklistComponent } from './first-run-checklist.component';
import { AuthService } from '../../../../services/auth.service';
import type { AuthStatus } from '../../../../services/auth.service';
import { TaskService } from '../../../../services/task.service';
import type { TaskDetail } from '../../../../models/task.model';
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
  const authStatus = signal<AuthStatus | null>(null);
  const deviceLogins = { startDeviceLogin: vi.fn(), deviceLoginStatus: vi.fn() };
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
    localStorage.removeItem('studio-first-run-git-setup-id');
    host.set([]);
    badges.set([]);
    authStatus.set({ authenticated: true, profile: 'networked', bootstrapRequired: false,
      user: { id: 'owner', username: 'owner', displayName: 'Owner', role: 'owner',
        projects: [], disabled: false, mustChangePassword: false } });
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(), provideHttpClientTesting(),
      { provide: AuthService, useValue: { status: authStatus } },
      { provide: TaskService, useValue: {
        createJob: vi.fn(() => of({ id: 'proof-local' })),
        getDetail: vi.fn(() => of({ info: { state: '2-ready' } })),
      } },
      { provide: WorkspaceManagerService, useValue: {} },
      { provide: WorkspaceOverlaysService, useValue: {} },
      { provide: RemoteHostsService, useValue: { hosts: host, reload: vi.fn() } },
      { provide: ProviderAuthStatusService, useValue: { statuses: badges, refresh: vi.fn(), ...deviceLogins } },
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
    const request = http.expectOne('/api/v1/projects/PROJ-1/tasks');
    expect(request.request.body.state).toBe('2-ready');
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

  it('reads local owner, registry, and checkout proof from the local backend', () => {
    authStatus.set({ authenticated: true, profile: 'local', bootstrapRequired: false });
    const fixture = TestBed.createComponent(FirstRunChecklistComponent);
    fixture.detectChanges();
    http.expectOne('/api/projects').flush([{ id: 'PROJ-1', displayName: 'Demo',
      shortCode: 'DM', workspaceId: 'ws-a', archived: false, storageLocation: '/tmp/demo',
      repositoryPath: '/tmp/repo', repositoryUrl: 'https://github.com/example/demo.git' }]);
    http.expectOne('/api/first-run/projects/PROJ-1/repository').flush({ registration: {
      projectId: 'PROJ-1', repositoryUrl: 'https://github.com/example/demo.git',
      integrationRef: 'main', releaseRef: null, deliveryPolicy: 'reviewed-publication',
    }, probes: [{ runnerId: 'local', admitted: true, verdict: 'admitted', detail: null,
      observedAt: new Date().toISOString() }] });
    const component = fixture.componentInstance;
    expect(component.ownerReady()).toBe(true);
    expect(component.repositoryReady()).toBe(true);
    expect(component.gitReady()).toBe(true);
    component.proofDetail.set({ info: { state: '4-auto-review',
      reviewProjection: { latestOutcome: 'Pass' },
      integration: { status: 'merged-locally', verification: {
        state: 'integrated-verified', sha: 'verified-integration-sha',
      } } } } as unknown as TaskDetail);
    expect(component.proofReviewed()).toBe(true);
    expect(component.proofIntegrated()).toBe(true);
    fixture.destroy();
  });

  it('keeps a registered local project pending until checkout is persisted', () => {
    authStatus.set({ authenticated: true, profile: 'local', bootstrapRequired: false });
    const fixture = TestBed.createComponent(FirstRunChecklistComponent);
    fixture.detectChanges();
    http.expectOne('/api/projects').flush([{ id: 'PROJ-1', displayName: 'Demo',
      shortCode: 'DM', workspaceId: 'ws-a', archived: false, storageLocation: '/tmp/demo' }]);
    http.expectOne('/api/first-run/projects/PROJ-1/repository').flush({ registration: {
      projectId: 'PROJ-1', repositoryUrl: 'https://github.com/example/demo.git',
      integrationRef: 'main', releaseRef: null, deliveryPolicy: 'reviewed-publication',
    }, probes: [] });
    expect(fixture.componentInstance.repositoryReady()).toBe(false);
    expect(fixture.componentInstance.gitSetupValid()).toBe(true);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="first-run-git-setup"]')).not.toBeNull();
    fixture.destroy();
  });

  it('starts the local runner after creating the proof card', () => {
    authStatus.set({ authenticated: true, profile: 'local', bootstrapRequired: false });
    host.set([{ id: 'runner-a', clientId: 'runner-a', status: 'online',
      lastHeartbeatAt: new Date().toISOString() } as RemoteHost]);
    badges.set([readyBadge()]);
    const fixture = TestBed.createComponent(FirstRunChecklistComponent);
    fixture.detectChanges();
    http.expectOne('/api/projects').flush([{ id: 'PROJ-1', displayName: 'Demo',
      shortCode: 'DM', workspaceId: 'ws-a', archived: false,
      storageLocation: '/tmp/demo', repositoryPath: '/tmp/repo' }]);
    http.expectOne('/api/first-run/projects/PROJ-1/repository').flush({ registration: {
      projectId: 'PROJ-1', repositoryUrl: 'https://github.com/example/demo.git',
      integrationRef: 'main', releaseRef: null, deliveryPolicy: 'reviewed-publication',
    }, probes: [{ runnerId: 'local', admitted: true, verdict: 'admitted', detail: null,
      observedAt: new Date().toISOString() }] });
    const component = fixture.componentInstance;
    component.demoRepositoryConfirmed.set(true);
    expect(component.canStartProof()).toBe(true);
    component.startProof();
    http.expectOne('/api/runner/Demo/start').flush({});
    expect(component.proof()?.id).toBe('proof-local');
    fixture.destroy();
  });

  it('refreshes repository status after registration during an active probe', () => {
    authStatus.set({ authenticated: true, profile: 'local', bootstrapRequired: false });
    const fixture = TestBed.createComponent(FirstRunChecklistComponent);
    fixture.detectChanges();
    const project = { id: 'PROJ-1', displayName: 'Demo', shortCode: 'DM',
      workspaceId: 'ws-a', archived: false, storageLocation: '/tmp/demo',
      repositoryPath: '/tmp/repo' };
    const origin = 'https://github.com/example/demo.git';
    http.expectOne('/api/projects').flush([project]);
    http.expectOne('/api/first-run/projects/PROJ-1/repository').flush({}, {
      status: 404, statusText: 'Pending',
    });
    const component = fixture.componentInstance;
    component.repositoryUrl.set(origin);
    component.reload();
    component.registerRepository();
    http.expectOne('/api/projects/PROJ-1').flush({});
    expect(component.repositoryReady()).toBe(false);
    http.expectOne('/api/projects').flush([project]);
    http.expectOne('/api/first-run/projects/PROJ-1/repository').flush({}, {
      status: 404, statusText: 'Pending',
    });
    http.expectOne('/api/projects').flush([{ ...project, repositoryUrl: origin }]);
    http.expectOne('/api/first-run/projects/PROJ-1/repository').flush({
      registration: { projectId: 'PROJ-1', repositoryUrl: origin,
        integrationRef: 'main', releaseRef: null, deliveryPolicy: 'reviewed-publication' },
      probes: [],
    });
    expect(component.repositoryReady()).toBe(true);
    fixture.destroy();
  });

  it('hands a token to the selected runner once and verifies without rereading it', () => {
    host.set([{ id: 'runner-a', clientId: 'runner-a', name: 'Runner A', status: 'online',
      lastHeartbeatAt: new Date().toISOString() } as RemoteHost]);
    const fixture = TestBed.createComponent(FirstRunChecklistComponent);
    fixture.detectChanges();
    http.expectOne('/api/v1/installation').flush({ installationId: 'install-a', ownerBootstrapped: true });
    http.expectOne('/api/v1/projects').flush([{ projectId: 'PROJ-1', workspaceId: 'ws-a',
      name: 'Demo', taskKeyPrefix: 'DM' }]);
    http.expectOne('/api/v1/projects/PROJ-1/repository').flush({ registration: {
      projectId: 'PROJ-1', repositoryUrl: 'https://github.com/example/demo.git',
      integrationRef: 'main', releaseRef: null, deliveryPolicy: 'reviewed-publication',
    }, probes: [] });
    const component = fixture.componentInstance;
    component.gitRunnerId.set('runner-a');
    component.gitMethod.set('token');
    const token = 'test-' + 'x'.repeat(32);
    component.gitToken.set(token);
    component.startGitSetup();
    const start = http.expectOne('/api/v1/management/runner-git-setups');
    expect(start.request.body.token).toBe(token);
    start.flush({ id: 'git-a', runnerId: 'runner-a', repositoryUrl: 'https://github.com/example/demo.git',
      method: 'token', state: 'awaiting-verify', publicKey: null,
      fetchVerified: false, pushVerified: false });
    expect(component.gitToken()).toBe('');
    expect(JSON.stringify(component.gitSetup())).not.toContain(token);
    component.verifyGitSetup();
    http.expectOne('/api/v1/management/runner-git-setups/git-a/verify').flush(null, { status: 202,
      statusText: 'Accepted' });
    http.expectOne('/api/v1/management/runner-git-setups/git-a').flush({
      id: 'git-a', runnerId: 'runner-a', repositoryUrl: 'https://github.com/example/demo.git',
      method: 'token', state: 'verifying', publicKey: null,
      fetchVerified: false, pushVerified: false,
    });
    fixture.destroy();
  });

  it('issues a one-time remote join token only for a valid HTTPS authority', () => {
    const fixture = TestBed.createComponent(FirstRunChecklistComponent);
    fixture.detectChanges();
    http.expectOne('/api/v1/installation').flush({ installationId: 'install-a', ownerBootstrapped: true });
    http.expectOne('/api/v1/projects').flush([]);
    const component = fixture.componentInstance;
    component.joinServerUrl.set('https://studio.example.test');
    component.joinRunnerName.set('linux-a');
    expect(component.joinValid()).toBe(true);
    component.generateJoinToken();
    const request = http.expectOne('/api/v1/management/host-join-tokens');
    expect(request.request.body.runnerName).toBe('linux-a');
    request.flush({ joinToken: 'aosj2-example', runnerId: 'linux-a', expiresAt: 'soon' });
    expect(component.joinCommand()).toContain('--join-token-file ./join.token');
    fixture.destroy();
  });

  it('starts local deploy-key setup through the local backend', () => {
    authStatus.set({ authenticated: true, profile: 'local', bootstrapRequired: false });
    const fixture = TestBed.createComponent(FirstRunChecklistComponent);
    fixture.detectChanges();
    http.expectOne('/api/projects').flush([{ id: 'PROJ-1', displayName: 'Demo',
      shortCode: 'DM', workspaceId: 'ws-a', archived: false, storageLocation: '/tmp/demo',
      repositoryPath: '/tmp/repo' }]);
    http.expectOne('/api/first-run/projects/PROJ-1/repository').flush({ registration: {
      projectId: 'PROJ-1', repositoryUrl: 'https://github.com/example/demo.git',
      integrationRef: 'main', releaseRef: null, deliveryPolicy: 'reviewed-publication',
    }, probes: [] });
    const component = fixture.componentInstance;
    expect(component.gitSetupValid()).toBe(true);
    component.startGitSetup();
    const request = http.expectOne('/api/first-run/git-setups');
    expect(request.request.body).toEqual({ projectId: 'PROJ-1', method: 'deploy-key', token: null });
    request.flush({ id: 'local-git-a', state: 'awaiting-github', publicKey: 'ssh-ed25519 sample',
      fetchVerified: false, pushVerified: false });
    expect(component.gitSetup()?.publicKey).toBe('ssh-ed25519 sample');
    fixture.destroy();
  });

  it('signs in through the shared Task Server device-login service of the Access page', () => {
    const fixture = TestBed.createComponent(FirstRunChecklistComponent);
    fixture.detectChanges();
    http.expectOne('/api/v1/installation').flush({ installationId: 'install-a', ownerBootstrapped: true });
    http.expectOne('/api/v1/projects').flush([]);
    const session = { handle: 'dlogin_a', state: 'queued' as const, verificationUrl: null, userCode: null,
      sessionKey: 'key-a', expiresAt: new Date(Date.now() + 900_000).toISOString() };
    deviceLogins.startDeviceLogin.mockReturnValue(of(session));
    deviceLogins.deviceLoginStatus.mockReturnValue(of({ ...session, state: 'running',
      detail: null, verificationUrl: 'https://claude.ai/oauth/authorize' }));
    const component = fixture.componentInstance;
    component.signIn({ ...readyBadge(), provider: 'claude', providerLabel: 'Claude', hostId: 'host-a' });
    expect(deviceLogins.startDeviceLogin).toHaveBeenCalledWith('host-a', 'claude');
    component.refreshDeviceLogin();
    expect(deviceLogins.deviceLoginStatus).toHaveBeenCalledWith('dlogin_a', 'key-a');
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-testid="first-run-device-login"]')?.textContent).toContain('Claude on Runner A: running');
    expect(host.querySelector('[data-testid="first-run-device-url"]')?.getAttribute('href'))
      .toBe('https://claude.ai/oauth/authorize');
    deviceLogins.deviceLoginStatus.mockReturnValue(of({ ...session, state: 'failed',
      detail: 'The sign-in timed out.' }));
    component.refreshDeviceLogin();
    fixture.detectChanges();
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('The sign-in timed out.');
    expect(host.querySelector('[data-testid="first-run-device-url"]')).toBeNull();
    fixture.destroy();
  });
});
