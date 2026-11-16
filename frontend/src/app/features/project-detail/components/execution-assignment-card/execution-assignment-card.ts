import { hostStatusIndicatorTone } from '../../../remote-hosts/models/remote-host.model';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { SegmentedControlComponent } from '../../../../components/segmented-control/segmented-control.component';
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
import { firstValueFrom } from 'rxjs';
import { RemoteHostsService, codingLocationUnavailableReason, type RemoteHost } from '../../../remote-hosts';
import { TaskService } from '../../../../services/task.service';

type ProbeState = 'pending' | 'running' | 'passed' | 'failed';
type ProbeKey = 'code' | 'branch' | 'toolchain' | 'noop';

interface ProbeCheck {
  key: ProbeKey;
  label: string;
  detail: string;
  state: ProbeState;
}

interface ProbeResult {
  key: ProbeKey;
  label: string;
  detail: string;
  passed: boolean;
}

interface ProjectExecutionSettings {
  localCodingAdmitted?: boolean;
  pickupMode?: 'auto' | 'manual' | 'paused';
  executionLocation?: string;
  executionRunner?: string | null;
  remoteExecutionEnabled?: boolean;
  integrationBranch?: string;
}

const PROBE_TONES: Record<ProbeState, StatusTone> = { passed: 'success', failed: 'danger', running: 'running', pending: 'neutral' };

export function probeCheckTone(state: ProbeState): StatusTone {
  return PROBE_TONES[state] ?? 'neutral';
}

@Component({
  selector: 'app-execution-assignment-card',
  standalone: true,
  imports: [StatusIndicatorComponent, SegmentedControlComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './execution-assignment-card.html',
  styleUrl: './execution-assignment-card.scss',
})
export class ExecutionAssignmentCardComponent implements OnInit {
  readonly projectName = input.required<string>();
  readonly hostTone = hostStatusIndicatorTone;
  readonly probeTone = probeCheckTone;

  private readonly http = inject(HttpClient);
  private readonly tasks = inject(TaskService);
  readonly hostRegistry = inject(RemoteHostsService);

  readonly selectedHostId = signal('local');
  readonly pickupMode = signal<'auto' | 'manual' | 'paused'>('manual');
  readonly localCodingAdmitted = signal(false);
  readonly integrationBranch = signal('');
  readonly saving = signal(false);
  readonly saveError = signal<string | null>(null);
  readonly saveConfirmation = signal<string | null>(null);
  readonly probing = signal(false);
  readonly checks = signal<ProbeCheck[]>(initialChecks());
  private probeGeneration = 0;

  readonly assignableHosts = computed(() =>
    this.hostRegistry.hosts().filter((host) => host.status !== 'retired'),
  );
  readonly unavailableReason = (host: RemoteHost) =>
    codingLocationUnavailableReason(host, this.localCodingAdmitted());
  readonly selectedHost = computed(() =>
    this.assignableHosts().find((host) => host.id === this.selectedHostId()) ?? null,
  );
  readonly deliveryPreflight = computed(() =>
    (this.selectedHost()?.projectPreflights ?? []).find(preflight =>
      preflight.projectName.localeCompare(this.projectName(), undefined, { sensitivity: 'accent' }) === 0
      && (!this.integrationBranch() || preflight.targetBranch === this.integrationBranch()),
    ) ?? null,
  );
  readonly latestProviderRefusal = computed(() =>
    this.hostRegistry.providerRefusals()[0] ?? null,
  );
  readonly probePassed = computed(() =>
    this.checks().length === 4 && this.checks().every((check) => check.state === 'passed'),
  );
  readonly pickupModes = [
    { id: 'auto' as const, label: 'Auto', hint: 'Offer ready tasks automatically at the selected location.' },
    { id: 'manual' as const, label: 'Manual', hint: 'Only explicit task starts are admitted.' },
    { id: 'paused' as const, label: 'Paused', hint: 'Hold automatic pickup while keeping the selected location.' },
  ];
  readonly segmentedPickupModes = this.pickupModes.map(mode => ({ value: mode.id, label: mode.label, tooltip: mode.hint, testid: 'project-pickup-mode-' + mode.id }));

  ngOnInit(): void {
    this.hostRegistry.ensureLoaded();
    this.http
      .get<Record<string, ProjectExecutionSettings>>('/api/projects/settings')
      .subscribe({
        next: (settings) => {
          const project = settings?.[this.projectName()];
          this.pickupMode.set(project?.pickupMode || 'manual');
          if (project?.pickupMode) this.tasks.recordProjectPickupMode(this.projectName(), project.pickupMode);
          this.localCodingAdmitted.set(project?.localCodingAdmitted ?? false);
          this.integrationBranch.set(project?.integrationBranch ?? '');
          this.selectedHostId.set(
            project?.executionLocation
            || (project?.remoteExecutionEnabled === false ? 'local' : project?.executionRunner)
            || 'local',
          );
        },
      });
  }

  setPickupMode(mode: 'auto' | 'manual' | 'paused'): void {
    if (mode === this.pickupMode() || this.saving()) return;
    const previous = this.pickupMode();
    this.pickupMode.set(mode);
    this.saving.set(true);
    this.saveError.set(null);
    this.saveConfirmation.set(null);
    this.http
      .put<ProjectExecutionSettings>(
        `/api/projects/${encodeURIComponent(this.projectName())}/execution-runner`,
        { pickupMode: mode },
      )
      .subscribe({
        next: (response) => {
          this.pickupMode.set(response.pickupMode || mode);
          this.tasks.recordProjectPickupMode(this.projectName(), this.pickupMode());
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
    const host = this.assignableHosts().find(candidate => candidate.id === hostId);
    if (!host || this.unavailableReason(host)) return;
    const previous = this.selectedHostId();
    this.selectedHostId.set(hostId);
    this.resetProbe();
    this.saving.set(true);
    this.saveError.set(null);
    this.saveConfirmation.set(null);
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
          this.saveConfirmation.set('Execution location saved.');
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

    this.checks.update(checks => checks.map(check => ({ ...check, state: 'running' as ProbeState, detail: 'Checking…' })));
    try {
      const results = await firstValueFrom(this.http.post<ProbeResult[]>(
        `/api/projects/${encodeURIComponent(this.projectName())}/execution-readiness-probe`,
        { hostId: host.id },
      ));
      if (generation !== this.probeGeneration) return;
      this.checks.set(results.map(result => ({
        key: result.key, label: result.label, detail: result.detail,
        state: (result.passed ? 'passed' : 'failed') as ProbeState,
      })));
    } catch {
      if (generation !== this.probeGeneration) return;
      this.checks.update(checks => checks.map(check => ({
        ...check, state: 'failed' as ProbeState, detail: 'Readiness probe could not reach the server.',
      })));
    }

    this.probing.set(false);
    this.log('probe-finished', { hostId: host.id, passed: this.probePassed() });
  }

  private resetProbe(): void {
    this.probeGeneration += 1;
    this.probing.set(false);
    this.checks.set(initialChecks());
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
    { key: 'branch', label: 'Integration branch', detail: 'The configured integration branch is available.', state: 'pending' },
    { key: 'toolchain', label: 'Toolchain', detail: 'The detected stack and a coding CLI are available.', state: 'pending' },
    { key: 'noop', label: 'Host readiness', detail: 'The coding host is ready to accept work.', state: 'pending' },
  ];
}
