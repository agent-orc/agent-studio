import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { forkJoin } from 'rxjs';
import { LoadingSurfaceComponent, PendingButtonDirective } from '../../../../components/async-feedback';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { clearVisibleInterval, setVisibleInterval, type VisibleIntervalHandle } from '../../../../utils/visible-interval';
import { historyRunStateTone } from '../../state/deploy-status-tone';
import { PrincipalNamePipe } from '../../../../pipes/principal-name.pipe';

interface RunbookDefinition { name: string; steps: string[]; environments: string[]; verify?: string | null }
interface RunbookStep { name: string; state: string; logRef: string }
interface RunbookVerification { target: string; passed: boolean; detail: string }
interface RunbookRun {
  id: string; name: string; state: string; requestedBy: string; approvedBy: string | null;
  requestedByName?: string; approvedByName?: string | null;
  verifies?: string | null; verification?: RunbookVerification | null;
  steps: RunbookStep[];
}
interface RunbookLogLine { step: string; message: string }
interface RunbookLog { lines: RunbookLogLine[] }
/** One step of the open log: its state and the lines the host and the control plane wrote for it. */
interface RunbookLogGroup { step: string; state: string | null; lines: string[] }

/** A run in these states can still change on the host, so the history keeps refreshing. */
const ACTIVE_STATES = new Set(['queued', 'running']);
export const RUNBOOK_POLL_MS = 5_000;

@Component({
  selector: 'app-project-runbooks',
  standalone: true,
  imports: [PrincipalNamePipe, DisclosureMarkerComponent, LoadingSurfaceComponent, PendingButtonDirective, StatusIndicatorComponent],
  templateUrl: './project-runbooks.html',
  styleUrl: './project-runbooks.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectRunbooksComponent {
  private readonly http = inject(HttpClient);
  /** Runbook history: a past failure stays calm (R4); a waiting approval reads as info. */
  readonly runTone = historyRunStateTone;
  readonly verificationLabel = verificationLabel;
  readonly projectName = input.required<string>();
  readonly environmentId = input.required<string>();
  readonly environmentKind = input.required<string>();
  readonly definitions = signal<RunbookDefinition[]>([]);
  readonly runs = signal<RunbookRun[]>([]);
  readonly loaded = signal(false);
  /** The action in flight, for example `start:restart` or `approve:<run id>`. */
  readonly pending = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly openLogId = signal<string | null>(null);
  readonly logLines = signal<RunbookLogLine[]>([]);
  readonly active = computed(() => this.runs().some(run => ACTIVE_STATES.has(run.state)));
  readonly logGroups = computed<RunbookLogGroup[]>(() => {
    const run = this.runs().find(item => item.id === this.openLogId());
    const groups = new Map<string, RunbookLogGroup>();
    for (const step of run?.steps ?? []) groups.set(step.name, { step: step.name, state: step.state, lines: [] });
    for (const line of this.logLines()) {
      const group = groups.get(line.step) ?? { step: line.step, state: null, lines: [] };
      group.lines.push(line.message);
      groups.set(line.step, group);
    }
    return [...groups.values()];
  });
  private timer: VisibleIntervalHandle | null = null;

  constructor() {
    effect(onCleanup => {
      const project = this.projectName();
      const environment = this.environmentId();
      this.loaded.set(false);
      const subscription = forkJoin({
        definitions: this.http.get<RunbookDefinition[]>(this.base(project)),
        runs: this.http.get<RunbookRun[]>(`${this.base(project)}/runs`, { params: { environmentId: environment } }),
      }).subscribe({
        next: result => {
          this.definitions.set(result.definitions.filter(item => item.environments.length === 0 || item.environments.includes(this.environmentKind())));
          this.runs.set(result.runs);
          this.error.set(null);
          this.loaded.set(true);
        },
        error: () => { this.error.set('Runbooks are unavailable.'); this.loaded.set(true); },
      });
      onCleanup(() => subscription.unsubscribe());
    });
    // Poll only while a run can still change; a settled history makes no requests (R4).
    effect(() => {
      if (this.active() && this.timer === null) this.timer = setVisibleInterval(() => this.refresh(), RUNBOOK_POLL_MS);
      else if (!this.active()) this.stopPolling();
    });
    inject(DestroyRef).onDestroy(() => this.stopPolling());
  }

  refresh(): void {
    this.http.get<RunbookRun[]>(`${this.base(this.projectName())}/runs`,
      { params: { environmentId: this.environmentId() } }).subscribe({
      next: runs => {
        this.runs.set(runs);
        this.error.set(null);
        const open = this.openLogId();
        if (open !== null) this.loadLog(open);
      },
      error: () => this.error.set('Runbook history could not be loaded.'),
    });
  }

  start(name: string): void {
    this.mutate(`start:${name}`, `${this.deployBase(this.projectName())}/environments/${encodeURIComponent(this.environmentId())}/runbooks/${encodeURIComponent(name)}/runs`);
  }

  decide(id: string, action: 'approve' | 'reject'): void {
    this.mutate(`${action}:${id}`, `${this.base(this.projectName())}/runs/${encodeURIComponent(id)}/${action}`);
  }

  isPending(key: string): boolean {
    return this.pending() === key;
  }

  toggleLog(id: string): void {
    if (this.openLogId() === id) { this.openLogId.set(null); return; }
    this.loadLog(id);
  }

  private loadLog(id: string): void {
    this.http.get<RunbookLog>(`${this.base(this.projectName())}/runs/${encodeURIComponent(id)}/log`).subscribe({
      next: log => { this.logLines.set(log.lines); this.openLogId.set(id); },
      error: () => this.error.set('Runbook log could not be loaded.'),
    });
  }

  private mutate(key: string, url: string): void {
    this.pending.set(key);
    this.http.post(url, {}).subscribe({
      next: () => { this.pending.set(null); this.refresh(); },
      error: () => { this.pending.set(null); this.error.set('The runbook action was refused. Refresh and check approvals.'); },
    });
  }

  private stopPolling(): void {
    clearVisibleInterval(this.timer);
    this.timer = null;
  }

  private base(project: string): string {
    return `${this.deployBase(project)}/runbooks`;
  }

  private deployBase(project: string): string {
    return `/api/projects/${encodeURIComponent(project)}/deploy`;
  }
}

/** `health:ready` reads as "health check ready", `alert:5xx-rate` as "alert rule 5xx-rate". */
export function verificationLabel(verify: string | null | undefined): string | null {
  if (!verify) return null;
  const [kind, target] = verify.split(':', 2);
  if (!target) return null;
  return kind === 'alert' ? `alert rule ${target}` : `health check ${target}`;
}
