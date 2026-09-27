import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { RunnerRecordedEvent } from '../../../../run-timeline';
import { isSimulated } from '../runner-event-replay';

@Component({
  selector: 'app-runner-replay-metadata',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './runner-replay-metadata.html',
  styleUrl: './runner-replay-metadata.scss',
})
export class RunnerReplayMetadataComponent {
  readonly events = input<readonly RunnerRecordedEvent[]>([]);
  readonly completions = computed(() => this.events().filter(
    event => event.kind === 'turn.completed' || event.kind === 'session.completed',
  ));

  /** True when any rendered turn came from a replayed trace rather than a run. */
  readonly simulated = computed(() => this.completions().some(isSimulated));

  label(event: RunnerRecordedEvent): string {
    return event.kind === 'turn.completed' ? 'Turn completed' : 'Session completed';
  }

  isSimulated(event: RunnerRecordedEvent): boolean {
    return isSimulated(event);
  }

  formatDuration(durationMs: number | null | undefined): string {
    if (durationMs == null) return '';
    const seconds = Math.max(0, Math.round(durationMs / 1000));
    const minutes = Math.floor(seconds / 60);
    return minutes > 0 ? `${minutes}m ${seconds % 60}s` : `${seconds}s`;
  }

  formatTokens(value: number | null | undefined): string {
    return value == null ? '' : new Intl.NumberFormat('en-US').format(value);
  }

  formatTime(timestamp: string): string {
    return new Date(timestamp).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }
}
