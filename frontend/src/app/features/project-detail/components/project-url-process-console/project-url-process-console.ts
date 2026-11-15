import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import type { ProjectUrlProcessSnapshot } from '../../../../models/task.model';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

const PROCESS_STATE_TONES: Readonly<Record<ProjectUrlProcessSnapshot['state'], StatusTone>> = {
  starting: 'running',
  running: 'success',
  stopped: 'neutral',
  exited: 'danger',
  failed: 'danger',
};

/**
 * Tone of a preview server process. A server is meant to keep running, so an
 * exit needs attention; a clean exit (code 0) is a warning, any other exit a failure.
 */
export function processStateTone(process: Pick<ProjectUrlProcessSnapshot, 'state' | 'exitCode'>): StatusTone {
  if (process.state === 'exited' && process.exitCode === 0) return 'warning';
  return PROCESS_STATE_TONES[process.state] ?? 'neutral';
}

/** Collapsible in-place console for a backend-owned project URL process. */
@Component({
  selector: 'app-project-url-process-console',
  standalone: true,
  imports: [PendingButtonDirective, StatusIndicatorComponent],
  templateUrl: './project-url-process-console.html',
  styleUrl: './project-url-process-console.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectUrlProcessConsoleComponent {
  readonly label = input.required<string>();
  readonly session = input<ProjectUrlProcessSnapshot | null>(null);
  readonly open = input(false);
  readonly stopping = input(false);
  readonly stopRequest = output<void>();
  readonly closeRequest = output<void>();

  readonly canStop = computed(() => {
    const state = this.session()?.state;
    return state === 'starting' || state === 'running' || state === 'failed';
  });

  readonly statusTone = processStateTone;

  statusLabel(process: ProjectUrlProcessSnapshot): string {
    switch (process.state) {
      case 'starting':
        return process.output.some(line => !line.startsWith('[studio]'))
          ? 'Starting · console active'
          : 'Starting · waiting for output';
      case 'running': return 'Running';
      case 'stopped': return 'Stopped';
      case 'exited': return 'Exited';
      default: return 'Failed';
    }
  }
}
