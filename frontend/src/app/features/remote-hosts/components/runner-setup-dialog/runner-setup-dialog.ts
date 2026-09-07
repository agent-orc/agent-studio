import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
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
import { ProviderSignInDialogService } from '../../services/codex-sign-in-dialog.service';

@Component({
  selector: 'app-runner-setup-dialog',
  standalone: true,
  imports: [FormsModule, VisibleCliTaskCardComponent],
  templateUrl: './runner-setup-dialog.html',
  styleUrl: './runner-setup-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunnerSetupDialogComponent implements OnInit {
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
  private readonly providerAuth = inject(ProviderAuthStatusService);
  private readonly providerSignIn = inject(ProviderSignInDialogService);

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
  readonly ready = computed(() => this.issues().length === 0);
  readonly request = computed(() => buildRunnerSetupRequest(this.host(), this.config()));
  readonly loopbackBlocked = computed(() => this.issues().some(issue => issue.startsWith('A remote host cannot reach')));

  ngOnInit(): void {
    const host = this.host();
    this.sshTarget.set(host.address ?? '');
    this.clientId.set(host.clientId || host.id);
  }

  setConnectionMode(value: string): void {
    if (value === 'central' || value === 'lan' || value === 'tunnel' || value === '') {
      this.connectionMode.set(value);
      if (value === 'tunnel' && /^http:\/\/(localhost|127\.0\.0\.1):5031\/?$/i.test(this.taskServerUrl().trim())) {
        this.taskServerUrl.set('http://127.0.0.1:15031');
      }
    }
  }

  openProviderSignIn(provider: 'claude' | 'codex'): void {
    const host = this.host();
    const aliases = [host.id, host.clientId, host.capacityHostId ?? '', host.name].filter(Boolean);
    const current = this.providerAuth.statuses().find(status =>
      status.provider === provider
      && status.aliases.some(alias => aliases.some(candidate => candidate.toLowerCase() === alias.toLowerCase())));
    this.providerSignIn.open({
      provider,
      hostId: host.capacityHostId ?? host.id,
      runnerId: host.id,
      hostName: host.name,
      aliases,
      sshTarget: this.sshTarget(),
      baselineAdvertisedAt: current?.advertisedAt ?? null,
    });
  }

  closeFromBackdrop(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.cancelled.emit();
  }
}
