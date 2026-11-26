import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { inject } from '@angular/core';
import { isRawRunnerId } from '../identity-chip/identity-display';
import { HostAliasService } from '../identity-chip/host-alias.service';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskExecutionLocation } from '../../models/task.model';
import { StatusIndicatorComponent, type StatusTone } from '../status-indicator/status-indicator.component';

type ExecutionState = TaskExecutionLocation['state'];

/** Live execution state to shared status tone (AGT-3403); unknown states are neutral. */
const EXECUTION_STATE_TONES: Readonly<Partial<Record<ExecutionState, StatusTone>>> = {
  'local-running': 'running',
  'remote-running': 'running',
  'queued-remote': 'neutral',
  'recovering': 'info',
  'remote-disconnected': 'danger',
  'remote-stale': 'danger',
  'no-active-execution': 'neutral',
};

/** Tone for an execution location; a historical attribution stays neutral (R4). */
export function executionLocationTone(value: TaskExecutionLocation | null | undefined): StatusTone {
  if (!value || value.historical) return 'neutral';
  return EXECUTION_STATE_TONES[value.state] ?? 'neutral';
}

@Component({
  selector: 'app-execution-location-badge',
  standalone: true,
  imports: [TooltipDirective, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './execution-location-badge.component.html',
  styleUrl: './execution-location-badge.component.scss',
})
export class ExecutionLocationBadgeComponent {
  readonly execution = input<TaskExecutionLocation | null | undefined>(null);
  readonly compact = input(true);
  /** AGT-4034: the status line above already names the host, so the badge does not repeat it. */
  readonly hostNamedElsewhere = input(false);
  private readonly aliases = inject(HostAliasService);
  private hostName(value: TaskExecutionLocation, id: string) {
    return this.aliases.display(id, isRawRunnerId(id) ? value.hostDisplayName : null).label;
  }
  /** The name, plus the id in parentheses when the name replaced a raw id (tooltip only). */
  private hostWithId(value: TaskExecutionLocation, id: string): string {
    const name = this.hostName(value, id);
    return name === id ? id : `${name} (${id})`;
  }
  readonly visible = computed(() => this.execution()?.state !== 'no-active-execution');
  readonly acute = computed(() => {
    const state = this.execution()?.state;
    return (state === 'remote-disconnected' || state === 'remote-stale') && !this.execution()?.historical;
  });
  readonly tone = computed(() => executionLocationTone(this.execution()));
  readonly label = computed(() => {
    const value = this.execution();
    if (!value) return '';
    if (value.state === 'recovering') return 'Recovering';
    if (value.executionKind === 'local') return 'Local';
    if (this.hostNamedElsewhere() && value.state === 'remote-running') return 'Host';
    const id = value.runnerId || value.hostDisplayName || value.configuredRunnerId || 'remote';
    return `Host · ${this.hostName(value, id)}`;
  });
  readonly stateLabel = computed(() => ({
    'local-running': 'Local running',
    'remote-running': 'Host running',
    'remote-disconnected': 'Host disconnected or stale',
    'remote-stale': 'Host stale - nothing is driving this run',
    'queued-remote': 'Queued for host',
    'recovering': 'Recovering ownership',
    'no-active-execution': 'No active execution',
  }[this.execution()?.state ?? 'no-active-execution']));
  readonly tooltip = computed(() => {
    const value = this.execution();
    if (!value) return '';
    const actual = value.executionKind === 'none'
      ? 'No active host'
      : `${value.executionKind === 'local' ? 'Local' : 'Host'}${value.runnerId ? ` · ${this.hostWithId(value, value.runnerId)}` : ''}`;
    const configured = value.configuredRunnerId ? `Host · ${this.hostWithId(value, value.configuredRunnerId)}` : 'Local';
    const lines = [
      this.stateLabel(),
      `Actual host: ${actual}`,
      `Configured host: ${configured}`,
      value.startedAt ? `Started: ${this.format(value.startedAt)}` : null,
      value.lastHeartbeat ? `Last heartbeat: ${this.format(value.lastHeartbeat)}` : null,
      value.lastActivityAt ? `Last activity: ${this.format(value.lastActivityAt)}` : null,
      value.branch ? `Branch: ${value.branch}` : null,
      value.worktreePath ? `Worktree: ${value.worktreePath}` : null,
      value.processId ? `Process: ${value.processId}` : null,
      value.sessionId ? `Session: ${value.sessionId}` : null,
      value.lastRejection
        ? `Latest rejection: Runner ${value.lastRejection.runnerName || value.lastRejection.runnerId} rejected: ${value.lastRejection.reason}`
        : null,
      value.lastRunnerEvent ? `Last runner event: ${value.lastRunnerEvent}` : null,
      `Connection: ${value.connectionState ?? 'none'}; lease: ${value.leaseState ?? 'none'}`,
      value.trustReason ? `Trusted because: ${value.trustReason}` : null,
    ];
    return lines.filter(Boolean).join('\n');
  });

  private format(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? value : date.toLocaleString('en-US');
  }
}
