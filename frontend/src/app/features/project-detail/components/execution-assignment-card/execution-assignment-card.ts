import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RemoteHostsService, type RemoteHost } from '../../../remote-hosts';

type ProbeState = 'pending' | 'running' | 'passed' | 'failed';
type ProbeKey = 'code' | 'branch' | 'toolchain' | 'noop';

interface ProbeCheck {
  key: ProbeKey;
  label: string;
  detail: string;
  state: ProbeState;
}

interface ProjectExecutionSettings {
  pickupMode?: 'auto' | 'manual' | 'paused';
  executionLocation?: string;
  executionRunner?: string | null;
  remoteExecutionEnabled?: boolean;
  integrationBranch?: string;
}

@Component({
  selector: 'app-execution-assignment-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './execution-assignment-card.html',
  styleUrl: './execution-assignment-card.scss',
})
export class ExecutionAssignmentCardComponent implements OnInit {
  readonly projectName = input.required<string>();

  private readonly http = inject(HttpClient);
  readonly hostRegistry = inject(RemoteHostsService);

  readonly selectedHostId = signal('local');
  readonly pickupMode = signal<'auto' | 'manual' | 'paused'>('manual');
  readonly integrationBranch = signal('develop');
  readonly saving = signal(false);
  readonly saveError = signal<string | null>(null);
  readonly probing = signal(false);
  readonly checks = signal<ProbeCheck[]>(initialChecks());
  private probeGeneration = 0;

  readonly assignableHosts = computed(() =>
    this.hostRegistry.hosts().filter((host) => host.status !== 'retired'),
  );
  readonly selectedHost = computed(() =>
    this.assignableHosts().find((host) => host.id === this.selectedHostId()) ?? null,
  );
  readonly deliveryPreflight = computed(() =>
    (this.selectedHost()?.projectPreflights ?? []).find(preflight =>
      preflight.projectName.localeCompare(this.projectName(), undefined, { sensitivity: 'accent' }) === 0,
    ) ?? null,
  );
  readonly latestProviderRefusal = computed(() =>
    this.hostRegistry.providerRefusals()[0] ?? null,
  );
  readonly probePassed = computed(() =>
    this.checks().every((check) => check.state === 'passed'),
  );
  readonly pickupModes = [
    { id: 'auto' as const, label: 'Auto', hint: 'Offer ready tasks automatically at the selected location.' },
    { id: 'manual' as const, label: 'Manual', hint: 'Only explicit task starts are admitted.' },
    { id: 'paused' as const, label: 'Paused', hint: 'Hold automatic pickup while keeping the selected location.' },
  ];

  ngOnInit(): void {
    this.hostRegistry.ensureLoaded();
    this.http
      .get<Record<string, ProjectExecutionSettings>>('/api/projects/settings')
      .subscribe({
        next: (settings) => {
          const project = settings?.[this.projectName()];
          this.pickupMode.set(project?.pickupMode || 'manual');
          this.selectedHostId.set(
            project?.executionLocation
            || (project?.remoteExecutionEnabled === false ? 'local' : project?.executionRunner)
            || 'local',
          );
          this.integrationBranch.set(project?.integrationBranch || 'develop');
        },
      });
  }

  setPickupMode(mode: 'auto' | 'manual' | 'paused'): void {
    if (mode === this.pickupMode() || this.saving()) return;
    const previous = this.pickupMode();
    this.pickupMode.set(mode);
    this.saving.set(true);
    this.saveError.set(null);
    this.http
      .put<ProjectExecutionSettings>(
        `/api/projects/${encodeURIComponent(this.projectName())}/execution-runner`,
        { pickupMode: mode },
      )
      .subscribe({
        next: (response) => {
          this.pickupMode.set(response.pickupMode || mode);
          this.saving.set(false);
          this.log('pickup-mode-saved', { pickupMode: this.pickupMode() });
        },
        error: () => {
          this.pickupMode.set(previous);
          this.saving.set(false);
          this.saveError.set('Pickup mode could not be saved. The previous mode is still active.');
          this.log('pickup-mode-failed', { requestedPickupMode: mode, restoredPickupMode: previous });
        },
      });
  }

  assign(hostId: string): void {
    const previous = this.selectedHostId();
    this.selectedHostId.set(hostId);
    this.resetProbe();
    this.saving.set(true);
    this.saveError.set(null);
    this.http
      .put<ProjectExecutionSettings>(
        `/api/projects/${encodeURIComponent(this.projectName())}/execution-runner`,
        {
          executionLocation: hostId,
        },
      )
      .subscribe({
        next: (response) => {
          this.selectedHostId.set(response.executionLocation || response.executionRunner || 'local');
          this.saving.set(false);
          this.log('assignment-saved', { hostId: this.selectedHostId() });
        },
        error: () => {
          this.selectedHostId.set(previous);
          this.saving.set(false);
          this.saveError.set('Assignment could not be saved. The previous location is still active.');
          this.log('assignment-failed', { requestedHostId: hostId, restoredHostId: previous });
        },
      });
  }

  async runProbe(): Promise<void> {
    const host = this.selectedHost();
    if (!host || this.probing()) return;

    const generation = ++this.probeGeneration;
    this.probing.set(true);
    this.saveError.set(null);
    this.checks.set(initialChecks());
    this.log('probe-started', { hostId: host.id });

    const outcomes: Record<ProbeKey, { passed: boolean; detail: string }> = {
      code: codeChannelOutcome(host),
      branch: branchOutcome(this.integrationBranch()),
      toolchain: toolchainOutcome(host),
      noop: noopOutcome(host),
    };

    for (const check of this.checks()) {
      if (generation !== this.probeGeneration) return;
      this.patchCheck(check.key, { state: 'running', detail: 'Checking…' });
      await pause(220);
      if (generation !== this.probeGeneration) return;
      const outcome = outcomes[check.key];
      this.patchCheck(check.key, {
        state: outcome.passed ? 'passed' : 'failed',
        detail: outcome.detail,
      });
    }

    this.probing.set(false);
    this.log('probe-finished', { hostId: host.id, passed: this.probePassed() });
  }

  private resetProbe(): void {
    this.probeGeneration += 1;
    this.probing.set(false);
    this.checks.set(initialChecks());
  }

  private patchCheck(key: ProbeKey, patch: Partial<ProbeCheck>): void {
    this.checks.update((checks) =>
      checks.map((check) => (check.key === key ? { ...check, ...patch } : check)),
    );
  }

  private log(event: string, detail: Record<string, unknown>): void {
    console.info(`[project-execution] ${event}`, {
      event: `project-execution.${event}`,
      project: this.projectName(),
      ...detail,
    });
  }
}

function initialChecks(): ProbeCheck[] {
  return [
    { key: 'code', label: 'Code channel', detail: 'Git origin access for this project.', state: 'pending' },
    { key: 'branch', label: 'Develop branch', detail: 'The project integration branch must be develop.', state: 'pending' },
    { key: 'toolchain', label: 'Toolchain', detail: 'Git, a build runtime, and a coding CLI are available.', state: 'pending' },
    { key: 'noop', label: 'No-op run', detail: 'Runner accepts a zero-change handshake without creating a task.', state: 'pending' },
  ];
}

function codeChannelOutcome(host: RemoteHost): { passed: boolean; detail: string } {
  const passed = host.capabilities.some((item) => item.toLowerCase() === 'git');
  return {
    passed,
    detail: passed ? 'Git code channel is available.' : 'This host does not advertise Git access.',
  };
}

function branchOutcome(branch: string): { passed: boolean; detail: string } {
  const passed = branch.trim().toLowerCase() === 'develop';
  return {
    passed,
    detail: passed ? 'Integration branch is develop.' : `Integration branch is ${branch || 'not configured'}; expected develop.`,
  };
}

function toolchainOutcome(host: RemoteHost): { passed: boolean; detail: string } {
  const capabilities = host.capabilities.map((item) => item.toLowerCase());
  const hasRuntime = capabilities.some((item) => item.startsWith('node ') || item.startsWith('dotnet '));
  const hasCli = host.cliQuotas.length > 0;
  const passed = capabilities.includes('git') && hasRuntime && hasCli;
  return {
    passed,
    detail: passed
      ? 'Git, build runtime, and coding CLI are reported ready.'
      : 'Git, build runtime, or coding CLI capability is missing.',
  };
}

function noopOutcome(host: RemoteHost): { passed: boolean; detail: string } {
  const passed = host.status === 'online' || host.status === 'idle';
  return {
    passed,
    detail: passed
      ? 'Runner heartbeat is healthy and can accept the no-op task.'
      : `Host is ${host.status} and cannot accept a probe.`,
  };
}

function pause(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
