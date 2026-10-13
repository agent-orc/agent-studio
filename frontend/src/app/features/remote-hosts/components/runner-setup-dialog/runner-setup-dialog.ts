import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import {
  VisibleCliTaskCardComponent,
  type VisibleCliTaskCreated,
  type VisibleCliTaskWorkspace,
} from '../../../visible-cli-task';
import type { RemoteHost } from '../../models/remote-host.model';
import {
  buildRunnerSetupRequest,
  runnerSetupIssues,
  type RunnerSetupConfig,
  type RunnerSetupConnectionMode,
} from '../../models/runner-setup.model';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';

type ProviderAuthEnvironmentVariable = 'CLAUDE_CODE_OAUTH_TOKEN' | 'ANTHROPIC_API_KEY';
type ProvisioningPhase = 'idle' | 'provisioning' | 'waiting' | 'ok' | 'unavailable' | 'error';

@Component({
  selector: 'app-runner-setup-dialog',
  standalone: true,
  imports: [FormsModule, VisibleCliTaskCardComponent],
  templateUrl: './runner-setup-dialog.html',
  styleUrl: './runner-setup-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunnerSetupDialogComponent implements OnInit, OnDestroy {
  readonly host = input.required<RemoteHost>();
  readonly workspaces = input<readonly VisibleCliTaskWorkspace[]>([]);
  readonly cancelled = output<void>();
  readonly taskCreated = output<VisibleCliTaskCreated>();

  readonly sshTarget = signal('');
  readonly taskServerUrl = signal('http://localhost:5031');
  readonly connectionMode = signal<RunnerSetupConnectionMode | ''>('');
  readonly clientId = signal('');
  readonly gitRemote = signal('');
  readonly gitPushRemote = signal('');
  readonly providerAuthEnvironmentVariable = signal<ProviderAuthEnvironmentVariable>('CLAUDE_CODE_OAUTH_TOKEN');
  readonly providerAuthSecret = signal('');
  readonly providerAuthPhase = signal<ProvisioningPhase>('idle');
  readonly providerAuthDetail = signal('No credential has been sent from this dialog.');
  readonly providerAuthBootstrapReady = signal(false);
  private readonly providerAuth = inject(ProviderAuthStatusService);
  private verificationSubscription: Subscription | null = null;

  readonly config = computed<RunnerSetupConfig>(() => ({
    sshTarget: this.sshTarget(),
    taskServerUrl: this.taskServerUrl(),
    connectionMode: this.connectionMode(),
    clientId: this.clientId(),
    gitRemote: this.gitRemote(),
    gitPushRemote: this.gitPushRemote(),
  }));
  readonly issues = computed(() => runnerSetupIssues(this.config()));
  readonly currentProviderAuth = computed(() => {
    const host = this.host();
    const aliases = new Set([
      host.id,
      host.clientId,
      host.capacityHostId ?? '',
      host.name,
    ].filter(Boolean).map(alias => alias.toLowerCase()));
    return this.providerAuth.statuses().find(status =>
      status.provider === 'claude'
      && status.aliases.some(alias => aliases.has(alias.toLowerCase()))) ?? null;
  });
  readonly providerAuthVerified = computed(() => this.currentProviderAuth()?.state === 'ok');
  readonly providerAuthGateSatisfied = computed(() =>
    this.providerAuthVerified() || this.providerAuthBootstrapReady());
  readonly ready = computed(() => this.issues().length === 0 && this.providerAuthGateSatisfied());
  readonly request = computed(() => buildRunnerSetupRequest(this.host(), this.config()));
  readonly loopbackBlocked = computed(() => this.issues().some(issue => issue.startsWith('A remote host cannot reach')));

  ngOnInit(): void {
    const host = this.host();
    this.sshTarget.set(host.address ?? '');
    this.clientId.set(host.clientId || host.id);
    if (this.providerAuthVerified()) {
      this.providerAuthPhase.set('ok');
      this.providerAuthDetail.set(this.currentProviderAuth()?.detail ?? 'The latest runner probe reports OK.');
    }
  }

  ngOnDestroy(): void {
    this.verificationSubscription?.unsubscribe();
  }

  setConnectionMode(value: string): void {
    if (value === 'central' || value === 'lan' || value === 'tunnel' || value === '') {
      this.connectionMode.set(value);
      if (value === 'tunnel' && /^http:\/\/(localhost|127\.0\.0\.1):5031\/?$/i.test(this.taskServerUrl().trim())) {
        this.taskServerUrl.set('http://127.0.0.1:15031');
      }
    }
  }

  setProviderAuthEnvironmentVariable(value: string): void {
    if (value !== 'CLAUDE_CODE_OAUTH_TOKEN' && value !== 'ANTHROPIC_API_KEY') return;
    this.providerAuthEnvironmentVariable.set(value);
    this.providerAuthSecret.set('');
    this.providerAuthBootstrapReady.set(false);
    this.providerAuthPhase.set('idle');
    this.providerAuthDetail.set('No credential has been sent from this dialog.');
  }

  provisionProviderAuth(): void {
    const secret = this.providerAuthSecret();
    const sshTarget = this.sshTarget().trim();
    if (this.providerAuthPhase() === 'provisioning' || !sshTarget || secret.length < 16) return;
    const baseline = this.currentProviderAuth()?.advertisedAt ?? null;
    const host = this.host();
    this.verificationSubscription?.unsubscribe();
    this.providerAuthPhase.set('provisioning');
    this.providerAuthDetail.set('Sending the credential through SSH stdin and installing the protected EnvironmentFile…');
    this.providerAuth.provision({
      sshTarget,
      runnerId: host.id,
      environmentVariable: this.providerAuthEnvironmentVariable(),
      secret,
      ...(this.currentProviderAuth() ? { idempotencyKey: `signin-${crypto.randomUUID()}` } : {}),
    }).subscribe({
      next: response => {
        this.providerAuthSecret.set('');
        this.providerAuthBootstrapReady.set(!response.processEnvironmentVerified);
        this.providerAuthPhase.set('waiting');
        this.providerAuthDetail.set(response.detail);
        const awaitRenewal = () => {
          if (!response.operationId) return;
          this.providerAuthDetail.set('Waiting for real requests from both runner units.');
          this.verificationSubscription = this.providerAuth.waitForRenewalCompletion(response.operationId, 75_000, sshTarget).subscribe({
            next: step => {
              this.providerAuthPhase.set(step === 'complete' ? 'ok' : 'unavailable');
              this.providerAuthDetail.set(step === 'complete'
                ? 'Both runner units verified the new provider generation with a real request.'
                : 'The host renewal needs recovery before another credential can be issued.');
            },
            error: () => this.providerAuthDetail.set(
              'The new generation is installed. Waiting for both runner units to complete a real request.'),
          });
        };
        if (!response.processEnvironmentVerified) {
          awaitRenewal();
          return;
        }
        this.verificationSubscription = this.providerAuth.waitForFreshProbe(
          'claude',
          [host.id, host.clientId, host.capacityHostId ?? '', host.name],
          baseline,
        ).subscribe({
          next: status => {
            if (status.state !== 'ok' || !response.operationId) {
              this.providerAuthPhase.set(status.state === 'ok' ? 'ok' : 'unavailable');
              this.providerAuthDetail.set(status.detail);
              return;
            }
            awaitRenewal();
          },
          error: () => {
            this.providerAuthPhase.set('waiting');
            this.providerAuthDetail.set(
              'The EnvironmentFile reached the daemon, but no newer provider probe arrived yet. The setup task can continue and will show the startup probe result.',
            );
          },
        });
      },
      error: error => {
        this.providerAuthSecret.set('');
        this.providerAuthBootstrapReady.set(false);
        this.providerAuthPhase.set('error');
        this.providerAuthDetail.set(
          error?.error?.message ?? 'Provider authentication could not be provisioned. No credential was retained by Studio.',
        );
      },
    });
  }

  closeFromBackdrop(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.cancelled.emit();
  }
}
