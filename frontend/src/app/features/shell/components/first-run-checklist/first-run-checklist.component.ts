import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, output, signal } from '@angular/core';
import { catchError, forkJoin, map, of, switchMap } from 'rxjs';
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

interface Probe<T> { value: T | null; error: boolean }
interface ProofCard { id: string; projectId: string; watchPath?: string; installationId: string }
interface NetworkProof {
  taskId: string;
  attempts: readonly { run: { resultSha?: string | null; finishedAt?: string | null } }[];
  integrations: readonly { reviewAttemptId?: string | null; status: string; integratedSha?: string | null }[];
}

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
  readonly registrationValid = computed(() => !!this.firstProject()?.shortCode
    && credentialFreeRepositoryUrl(this.repositoryUrl()) && !!this.integrationRef().trim());

  readonly firstProject = computed(() => this.projects().value?.find(project => !project.archived) ?? null);
  readonly ownerReady = computed(() => this.identity().value?.ownerBootstrapped === true
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
  readonly repositoryReady = computed(() => !!this.firstProject()?.id && !!this.repository().value?.registration);
  readonly gitReady = computed(() => {
    const status = this.repository().value;
    if (!status) return false;
    return latestRepositoryProbes(status).some(probe => probe.admitted
      && this.nowMs() - Date.parse(probe.observedAt) < 24 * 60 * 60_000
      && this.activeHosts().some(host => host.clientId === probe.runnerId || host.id === probe.runnerId));
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
    this.poll = setInterval(() => { this.reload(); this.refreshProof(); }, 30_000);
  }

  ngOnDestroy(): void { if (this.poll) clearInterval(this.poll); }

  reload(): void {
    if (this.loading()) return;
    this.nowMs.set(Date.now());
    this.loading.set(true);
    this.hosts.reload();
    this.providers.refresh();
    const identity = this.http.get<InstallationIdentity>('/api/v1/installation').pipe(
      map(value => ({ value, error: false } as Probe<InstallationIdentity>)),
      catchError(() => of({ value: null, error: true } as Probe<InstallationIdentity>)));
    const projects = this.http.get<ProjectRegistryRead[]>('/api/v1/projects').pipe(
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
      return this.http.get<RepositoryStatus>(`/api/v1/projects/${encodeURIComponent(project.id)}/repository`).pipe(
        map(value => ({ value, error: false } as Probe<RepositoryStatus>)),
        catchError(() => of({ value: null, error: true } as Probe<RepositoryStatus>)));
    })).subscribe({
      next: repository => { this.repository.set(repository); this.loading.set(false); this.refreshProof(); },
      error: () => this.loading.set(false),
    });
  }

  openProject(): void {
    const project = this.firstProject();
    if (project) this.openProjectSettings.emit(project.displayName);
    else this.workspaces.openProjectOnboard('');
  }

  openHosts(): void { this.overlays.select('remote-hosts'); }

  registerRepository(): void {
    const project = this.firstProject();
    if (!project || this.registering()) return;
    this.registering.set(true);
    this.registrationError.set(null);
    this.http.post('/api/v1/projects/registrations', {
      projectId: project.id,
      workspaceId: project.workspaceId,
      name: project.displayName,
      taskKeyPrefix: project.shortCode,
      repositoryUrl: this.repositoryUrl().trim(),
      integrationRef: this.integrationRef().trim(),
      deliveryPolicy: 'reviewed-publication',
    }).subscribe({
      next: () => { this.registering.set(false); this.repositoryUrl.set(''); this.reload(); },
      error: () => {
        this.registering.set(false);
        this.registrationError.set('Repository registration failed. Check the URL, branch, and owner access.');
      },
    });
  }

  signIn(badge: ProviderAuthBadge): void {
    if (badge.provider === 'codex') this.codexSignIn.open(signInTarget(badge));
    if (badge.provider === 'claude') this.claudeSignIn.open(claudeSignInTarget(badge));
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
    const watchPath = project.storageLocation;
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
      : this.http.post<{ taskId: string }>('/api/v1/studio/tasks', {
        projectId: project.id, title: 'First-run proof: add a project overview', body: prompt, state: '2-ready',
      }).pipe(map(response => response.taskId));
    creation.subscribe({
      next: id => {
        const proof = { id, projectId: project.id, watchPath,
          installationId: this.identity().value!.installationId };
        this.proof.set(proof);
        localStorage.setItem('studio-first-run-proof', JSON.stringify(proof));
        this.creatingProof.set(false);
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

export function credentialFreeRepositoryUrl(value: string): boolean {
  const trimmed = value.trim();
  if (/^git@[A-Za-z0-9.-]+:[A-Za-z0-9._/-]+$/.test(trimmed)
      && !trimmed.includes('..')) return true;
  try {
    const url = new URL(trimmed);
    return ['https:', 'ssh:'].includes(url.protocol)
      && !!url.hostname && !!url.pathname.replaceAll('/', '')
      && (url.protocol !== 'ssh:' ? !url.username : !url.username || url.username === 'git')
      && !url.password && !url.search && !url.hash;
  } catch { return false; }
}
