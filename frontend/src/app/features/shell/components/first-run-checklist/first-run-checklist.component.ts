import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, output, signal } from '@angular/core';
import { catchError, forkJoin, map, of, switchMap } from 'rxjs';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { AuthService } from '../../../../services/auth.service';
import { TaskService } from '../../../../services/task.service';
import type { TaskDetail } from '../../../../models/task.model';
import { WorkspaceManagerService } from '../../state/workspace-manager.service';
import { WorkspaceOverlaysService } from '../../state/workspace-overlays.service';
import { RemoteHostsService } from '../../../remote-hosts/services/remote-hosts.service';
import { ProviderAuthStatusService } from '../../../remote-hosts/services/provider-auth-status.service';
import { CodexSignInDialogService } from '../../../remote-hosts/services/codex-sign-in-dialog.service';
import { ClaudeSignInDialogService } from '../../../remote-hosts/services/claude-sign-in-dialog.service';
import { claudeSignInTarget, signInTarget } from '../../../remote-hosts/models/provider-auth.model';
import type { ProviderAuthBadge } from '../../../remote-hosts/models/provider-auth.model';
import { latestRepositoryProbes } from '../../../remote-hosts/models/deployment-checkpoints';
import { normalizeProjectRegistryEntries } from '../../../remote-hosts/models/deployment-checkpoints';
import type { InstallationIdentity, ProjectRegistryEntry, ProjectRegistryRead, RepositoryStatus } from '../../../remote-hosts/models/deployment-checkpoints';
import { credentialFreeRepositoryUrl } from './first-run-checklist.models';
import type { Probe, ProofCard, NetworkProof, DeviceLogin, HostJoinToken, GitSetup } from './first-run-checklist.models';

/** Installation facts are re-read from the authority and runner probes on every refresh. */
@Component({
  selector: 'app-first-run-checklist',
  standalone: true,
  templateUrl: './first-run-checklist.component.html',
  styleUrl: './first-run-checklist.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FirstRunChecklistComponent implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly tasks = inject(TaskService);
  private readonly workspaces = inject(WorkspaceManagerService);
  private readonly overlays = inject(WorkspaceOverlaysService);
  private readonly hosts = inject(RemoteHostsService);
  private readonly providers = inject(ProviderAuthStatusService);
  private readonly codexSignIn = inject(CodexSignInDialogService);
  private readonly claudeSignIn = inject(ClaudeSignInDialogService);
  private poll: ReturnType<typeof setInterval> | null = null;
  private loginPoll: ReturnType<typeof setInterval> | null = null;
  private gitPoll: ReturnType<typeof setInterval> | null = null;
  private reloadQueued = false;
  readonly openProjectSettings = output<string>();

  readonly loading = signal(false);
  readonly nowMs = signal(Date.now());
  readonly identity = signal<Probe<InstallationIdentity>>({ value: null, error: false });
  readonly projects = signal<Probe<readonly ProjectRegistryEntry[]>>({ value: null, error: false });
  readonly repository = signal<Probe<RepositoryStatus>>({ value: null, error: false });
  readonly proof = signal<ProofCard | null>(this.savedProof());
  readonly proofDetail = signal<TaskDetail | null>(null);
  readonly networkProof = signal<NetworkProof | null>(null);
  readonly proofError = signal<string | null>(null);
  readonly creatingProof = signal(false);
  readonly demoRepositoryConfirmed = signal(false);
  readonly repositoryUrl = signal('');
  readonly integrationRef = signal('main');
  readonly registering = signal(false);
  readonly registrationError = signal<string | null>(null);
  readonly deviceLogin = signal<DeviceLogin | null>(null);
  readonly deviceLoginError = signal<string | null>(null);
  readonly codeCopyLabel = signal('Copy code');
  readonly joinServerUrl = signal('');
  readonly joinRunnerName = signal('first-run-host');
  readonly joinRole = signal<'coding' | 'review'>('coding');
  readonly joinToken = signal<HostJoinToken | null>(null);
  readonly joinCopyLabel = signal('Copy command');
  readonly joinError = signal<string | null>(null);
  readonly joining = signal(false);
  readonly joinValid = computed(() => /^https:\/\/[^\s/@?#]+\/?$/.test(this.joinServerUrl().trim())
    && /^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$/.test(this.joinRunnerName().trim()));
  readonly joinCommand = computed(() => `chmod 600 ./join.token && sudo ./agent-studio-setup --mode agent-host --join-token-file ./join.token --runner-name ${this.joinRunnerName().trim()} --role ${this.joinRole()}`);
  readonly registrationValid = computed(() => !!this.firstProject()?.shortCode
    && credentialFreeRepositoryUrl(this.repositoryUrl())
    && (!this.localProfile() || this.repositoryUrl().trim().startsWith('https://'))
    && !!this.integrationRef().trim());
  readonly gitMethod = signal<'token' | 'deploy-key'>('deploy-key');
  readonly gitToken = signal('');
  readonly gitRunnerId = signal('');
  readonly gitSetup = signal<GitSetup | null>(null);
  readonly gitSetupError = signal<string | null>(null);
  readonly gitStarting = signal(false);
  readonly keyCopyLabel = signal('Copy public key');
  readonly gitSetupValid = computed(() => (this.localProfile() || !!this.gitRunnerId())
    && !!this.repository().value?.registration?.repositoryUrl
    && (this.gitMethod() === 'deploy-key' || this.gitToken().length >= 20));

  readonly firstProject = computed(() => this.projects().value?.find(project => !project.archived) ?? null);
  readonly localProfile = computed(() => this.auth.status()?.profile === 'local');
  readonly ownerReady = computed(() => (this.localProfile()
      ? this.auth.status()?.bootstrapRequired === false
      : this.identity().value?.ownerBootstrapped === true)
    && this.auth.status()?.authenticated === true
    && (this.auth.status()?.profile === 'local' || this.auth.status()?.user?.role === 'owner'));
  readonly activeHosts = computed(() => this.hosts.hosts().filter(host =>
    host.status === 'online' && !!host.lastHeartbeatAt
    && this.nowMs() - Date.parse(host.lastHeartbeatAt) < 90_000));
  readonly providerBadges = computed(() => this.providers.statuses());
  readonly providersReady = computed(() => {
    const required = this.providerBadges().filter(badge =>
      (badge.provider === 'codex' || badge.provider === 'claude') && this.badgeHostOnline(badge));
    return required.length > 0 && required.every(badge => this.badgeReady(badge));
  });
  readonly selectedProvider = computed(() => this.providerBadges().find(badge =>
    (badge.provider === 'codex' || badge.provider === 'claude') && this.badgeHostOnline(badge)
    && this.badgeReady(badge)) ?? null);
  readonly repositoryReady = computed(() => !!this.firstProject()?.id && !!this.repository().value?.registration
    && (!this.localProfile() || !!this.firstProject()?.repositoryPath));
  readonly gitReady = computed(() => {
    const status = this.repository().value;
    if (!status) return false;
    const probes = latestRepositoryProbes(status).filter(probe => probe.admitted
      && this.nowMs() - Date.parse(probe.observedAt) < 10 * 60_000);
    if (this.localProfile()) return probes.length > 0;
    const executionHosts = this.activeHosts().filter(host =>
      !host.serviceRole || ['coding', 'review', 'runner'].includes(host.serviceRole));
    return executionHosts.length > 0 && executionHosts.every(host => probes.some(probe =>
      host.clientId === probe.runnerId || host.id === probe.runnerId));
  });
  readonly canStartProof = computed(() => this.ownerReady() && this.providersReady()
    && this.repositoryReady() && this.gitReady() && this.activeHosts().length > 0
    && this.demoRepositoryConfirmed()
    && !this.proof() && !this.creatingProof());
  readonly proofRan = computed(() => this.networkProof()?.attempts.some(attempt =>
    !!attempt.run.finishedAt && !!attempt.run.resultSha) === true
    || (!!this.proofDetail()?.info
      && this.proofDetail()!.info.state !== '2-ready' && this.proofDetail()!.info.state !== '1-preparation'));
  readonly proofReviewed = computed(() => {
    const state = this.proofDetail()?.info.state;
    return this.networkProof()?.integrations.some(command => !!command.reviewAttemptId) === true
      || (!!state && ['6-completed', '7-archive'].includes(state));
  });
  readonly proofIntegrated = computed(() => this.networkProof()?.integrations.some(command =>
    command.status === 'integrated' && !!command.integratedSha) === true
    || this.proofDetail()?.info.integration?.status === 'integrated');

  ngOnInit(): void {
    this.reload();
    if (localStorage.getItem('studio-first-run-git-setup-id')) this.refreshGitSetup();
    this.poll = setInterval(() => { this.reload(); this.refreshProof(); }, 30_000);
  }
  ngOnDestroy(): void {
    if (this.poll) clearInterval(this.poll);
    if (this.loginPoll) clearInterval(this.loginPoll);
    if (this.gitPoll) clearInterval(this.gitPoll);
  }

  reload(): void {
    if (this.loading()) { this.reloadQueued = true; return; }
    this.nowMs.set(Date.now());
    this.loading.set(true);
    this.hosts.reload();
    this.providers.refresh();
    const identity = (this.localProfile()
      ? of({ installationId: 'local', ownerBootstrapped: this.auth.status()?.bootstrapRequired === false,
          ownerBootstrapArmed: false } as InstallationIdentity)
      : this.http.get<InstallationIdentity>('/api/v1/installation')).pipe(
      map(value => ({ value, error: false } as Probe<InstallationIdentity>)),
      catchError(() => of({ value: null, error: true } as Probe<InstallationIdentity>)));
    const projects = this.http.get<ProjectRegistryRead[]>(this.localProfile() ? '/api/projects' : '/api/v1/projects').pipe(
      map(value => ({ value: normalizeProjectRegistryEntries(value), error: false } as Probe<readonly ProjectRegistryEntry[]>)),
      catchError(() => of({ value: null, error: true } as Probe<readonly ProjectRegistryEntry[]>)));
    forkJoin({ identity, projects }).pipe(switchMap(result => {
      this.identity.set(result.identity);
      this.projects.set(result.projects);
      if (this.proof() && result.identity.value
          && this.proof()!.installationId !== result.identity.value.installationId) {
        this.proof.set(null);
        this.proofDetail.set(null);
        this.networkProof.set(null);
        localStorage.removeItem('studio-first-run-proof');
      }
      const project = result.projects.value?.find(item => !item.archived);
      if (!project) return of({ value: null, error: false } as Probe<RepositoryStatus>);
      const repositoryUrl = this.localProfile()
        ? `/api/first-run/projects/${encodeURIComponent(project.id)}/repository`
        : `/api/v1/projects/${encodeURIComponent(project.id)}/repository`;
      return this.http.get<RepositoryStatus>(repositoryUrl).pipe(
        map(value => ({ value, error: false } as Probe<RepositoryStatus>)),
        catchError(() => of({ value: null, error: true } as Probe<RepositoryStatus>)));
    })).subscribe({
      next: repository => { this.repository.set(repository); this.finishReload(); this.refreshProof(); },
      error: () => this.finishReload(),
    });
  }
  private finishReload(): void {
    this.loading.set(false);
    if (this.reloadQueued) { this.reloadQueued = false; this.reload(); }
  }
  openProject(): void {
    const project = this.firstProject();
    if (project) this.openProjectSettings.emit(project.displayName);
    else this.workspaces.openProjectOnboard('');
  }
  openHosts(): void { this.overlays.select('remote-hosts'); }
  startGitSetup(): void {
    if (!this.gitSetupValid() || this.gitStarting()) return;
    this.gitStarting.set(true);
    this.gitSetupError.set(null);
    const local = this.localProfile();
    const url = local ? '/api/first-run/git-setups' : '/api/v1/management/runner-git-setups';
    const body = local ? {
      projectId: this.firstProject()!.id, method: this.gitMethod(),
      token: this.gitMethod() === 'token' ? this.gitToken() : null,
    } : {
      runnerId: this.gitRunnerId(), repositoryUrl: this.repository().value!.registration!.repositoryUrl,
      method: this.gitMethod(), token: this.gitMethod() === 'token' ? this.gitToken() : null,
    };
    this.http.post<GitSetup>(url, body).subscribe({
      next: setup => {
        this.gitToken.set('');
        this.gitSetup.set(setup);
        localStorage.setItem('studio-first-run-git-setup-id', setup.id);
        this.gitStarting.set(false);
        if (this.gitPoll) clearInterval(this.gitPoll);
        this.gitPoll = setInterval(() => this.refreshGitSetup(), 2_000);
      },
      error: () => {
        this.gitToken.set('');
        this.gitStarting.set(false);
        this.gitSetupError.set('GitHub access setup could not start on this runner.');
      },
    });
  }
  refreshGitSetup(): void {
    const id = this.gitSetup()?.id ?? localStorage.getItem('studio-first-run-git-setup-id');
    if (!id) return;
    const root = this.localProfile() ? '/api/first-run/git-setups'
      : '/api/v1/management/runner-git-setups';
    this.http.get<GitSetup>(`${root}/${encodeURIComponent(id)}`)
      .subscribe({
        next: setup => {
          this.gitSetup.set(setup);
          if (setup.state === 'verified') {
            localStorage.removeItem('studio-first-run-git-setup-id');
            this.reload();
          }
          if (['verified', 'failed', 'expired'].includes(setup.state) && this.gitPoll) {
            clearInterval(this.gitPoll);
            this.gitPoll = null;
          }
        },
        error: () => {
          localStorage.removeItem('studio-first-run-git-setup-id');
          this.gitSetupError.set('GitHub access status is unavailable. Start setup again if the Task Server restarted.');
        },
      });
  }

  verifyGitSetup(): void {
    const id = this.gitSetup()?.id;
    if (!id) return;
    const root = this.localProfile() ? '/api/first-run/git-setups'
      : '/api/v1/management/runner-git-setups';
    this.http.post(`${root}/${encodeURIComponent(id)}/verify`, {})
      .subscribe({
        next: () => this.refreshGitSetup(),
        error: () => this.gitSetupError.set('Read and write verification could not start.'),
      });
  }
  async copyDeployKey(): Promise<void> {
    const key = this.gitSetup()?.publicKey;
    if (key) this.keyCopyLabel.set(await copyTextToClipboard(key) ? 'Copied' : 'Copy failed');
  }

  generateJoinToken(): void {
    if (!this.joinValid() || this.joining()) return;
    this.joining.set(true);
    this.joinToken.set(null);
    this.joinError.set(null);
    this.http.post<HostJoinToken>('/api/v1/management/host-join-tokens', {
      serverUrl: this.joinServerUrl().trim(), runnerName: this.joinRunnerName().trim(),
      role: this.joinRole(),
    }).subscribe({
      next: issued => { this.joinToken.set(issued); this.joining.set(false); },
      error: () => {
        this.joinError.set('The join token could not be issued. Check the host name and owner session.');
        this.joining.set(false);
      },
    });
  }
  downloadJoinToken(): void {
    const token = this.joinToken()?.joinToken;
    if (!token) return;
    const blobUrl = URL.createObjectURL(new Blob([token + '\n'], { type: 'text/plain' }));
    const link = document.createElement('a');
    link.href = blobUrl;
    link.download = 'join.token';
    link.click();
    URL.revokeObjectURL(blobUrl);
  }

  async copyJoinCommand(): Promise<void> {
    this.joinCopyLabel.set(await copyTextToClipboard(this.joinCommand()) ? 'Copied' : 'Copy failed');
  }

  registerRepository(): void {
    const project = this.firstProject();
    if (!project || this.registering()) return;
    this.registering.set(true);
    this.registrationError.set(null);
    const url = this.localProfile()
      ? `/api/projects/${encodeURIComponent(project.id)}` : '/api/v1/projects/registrations';
    const body = this.localProfile() ? { repositoryUrl: this.repositoryUrl().trim() } : {
      projectId: project.id,
      workspaceId: project.workspaceId,
      name: project.displayName,
      taskKeyPrefix: project.shortCode,
      repositoryUrl: this.repositoryUrl().trim(),
      integrationRef: this.integrationRef().trim(),
      deliveryPolicy: 'reviewed-publication',
    };
    this.http.request(this.localProfile() ? 'PUT' : 'POST', url, { body }).subscribe({
      next: () => { this.registering.set(false); this.repositoryUrl.set(''); this.reload(); },
      error: () => {
        this.registering.set(false);
        this.registrationError.set('Repository registration failed. Check the URL, branch, and owner access.');
      },
    });
  }

  signIn(badge: ProviderAuthBadge): void {
    if (!this.localProfile()) {
      this.deviceLoginError.set(null);
      this.http.post<DeviceLogin>('/api/v1/management/runner-device-logins', {
        runnerId: badge.runnerId, provider: badge.provider,
      }).subscribe({
        next: login => {
          this.deviceLogin.set(login);
          this.codeCopyLabel.set('Copy code');
          if (this.loginPoll) clearInterval(this.loginPoll);
          this.loginPoll = setInterval(() => this.refreshDeviceLogin(), 1_000);
        },
        error: () => this.deviceLoginError.set('Device sign-in could not start on this runner.'),
      });
      return;
    }
    if (badge.provider === 'codex') this.codexSignIn.open(signInTarget(badge));
    if (badge.provider === 'claude') this.claudeSignIn.open(claudeSignInTarget(badge));
  }

  refreshDeviceLogin(): void {
    const login = this.deviceLogin();
    if (!login) return;
    this.http.get<DeviceLogin>(`/api/v1/management/runner-device-logins/${encodeURIComponent(login.id)}`)
      .subscribe({
        next: update => {
          this.deviceLogin.set(update);
          if (update.state === 'completed') { this.providers.refresh(); this.reload(); }
          if (['completed', 'failed', 'expired'].includes(update.state) && this.loginPoll) {
            clearInterval(this.loginPoll);
            this.loginPoll = null;
          }
        },
        error: () => this.deviceLoginError.set('Device sign-in status is unavailable. Refresh to retry.'),
      });
  }

  async copyDeviceCode(): Promise<void> {
    const code = this.deviceLogin()?.userCode;
    if (code) this.codeCopyLabel.set(await copyTextToClipboard(code) ? 'Copied' : 'Copy failed');
  }

  badgeHostOnline(badge: ProviderAuthBadge): boolean {
    return this.activeHosts().some(host => badge.aliases.some(alias =>
      alias === host.id || alias === host.clientId || alias === host.capacityHostId));
  }

  badgeReady(badge: ProviderAuthBadge): boolean {
    return badge.reachable && badge.state === 'ok' && !!badge.lastSeenAt
      && this.nowMs() - Date.parse(badge.lastSeenAt) < 5 * 60_000;
  }

  startProof(): void {
    if (!this.canStartProof()) return;
    const project = this.firstProject()!;
    const provider = this.selectedProvider()!;
    const watchPath = this.localProfile() ? project.storageLocation : undefined;
    this.creatingProof.set(true);
    this.proofError.set(null);
    const prompt = 'Read the README and top-level folders. Add docs/project-overview.md with a short overview of the project and how to run it. Keep it under 80 lines. End your reply with [[TASK_DONE]].';
    const creation = watchPath ? this.tasks.createJob({
      title: 'First-run proof: add a project overview',
      watchPath,
      agent: provider.provider,
      cliType: provider.provider as 'codex' | 'claude',
      targetState: '2-ready',
      taskType: 'chore',
      requiresIntegration: true,
      promptMarkdown: prompt,
    }).pipe(map(response => response.id))
      : this.http.post<{ taskId: string }>(`/api/v1/projects/${encodeURIComponent(project.id)}/tasks`, {
        title: 'First-run proof: add a project overview', body: prompt, state: '2-ready',
      }).pipe(map(response => response.taskId));
    creation.subscribe({
      next: id => {
        const proof = { id, projectId: project.id, watchPath,
          installationId: this.identity().value!.installationId };
        this.proof.set(proof);
        localStorage.setItem('studio-first-run-proof', JSON.stringify(proof));
        this.creatingProof.set(false);
        if (watchPath) this.http.post(`/api/runner/${encodeURIComponent(project.displayName)}/start`, {}).subscribe({
          error: () => this.proofError.set('The sample card was created, but its local runner could not start.'),
        });
        this.refreshProof();
      },
      error: () => {
        this.proofError.set('The sample card could not be created. Check project routing and try again.');
        this.creatingProof.set(false);
      },
    });
  }

  refreshProof(): void {
    const proof = this.proof();
    if (!proof) return;
    if (!proof.watchPath) {
      const root = `/api/v1/projects/${encodeURIComponent(proof.projectId)}/tasks/${encodeURIComponent(proof.id)}`;
      forkJoin({
        task: this.http.get<{ taskId: string }>(root),
        attempts: this.http.get<NetworkProof['attempts']>(`${root}/attempts`),
        integrations: this.http.get<NetworkProof['integrations']>(`/api/v1/integrations?taskId=${encodeURIComponent(proof.id)}`),
      }).subscribe({
        next: value => {
          this.networkProof.set({ taskId: value.task.taskId, attempts: value.attempts,
            integrations: value.integrations.filter(command => !!command.reviewAttemptId) });
          this.proofError.set(null);
        },
        error: () => {
          this.networkProof.set(null);
          this.proofError.set('The proof card could not be read. Retry after the Task Server reconnects.');
        },
      });
      return;
    }
    this.tasks.getDetail(proof.id, proof.watchPath, proof.projectId).subscribe({
      next: detail => { this.proofDetail.set(detail); this.proofError.set(null); },
      error: () => this.proofError.set('The proof card could not be read. Retry after the Task Server reconnects.'),
    });
  }

  private savedProof(): ProofCard | null {
    try {
      const value = JSON.parse(localStorage.getItem('studio-first-run-proof') ?? 'null') as ProofCard | null;
      return value?.id && value.projectId && value.installationId ? value : null;
    } catch { return null; }
  }
}
