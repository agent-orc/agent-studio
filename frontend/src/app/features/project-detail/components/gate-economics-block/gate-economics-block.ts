import { SegmentedControlComponent } from '../../../../components/segmented-control/segmented-control.component';
import { ChangeDetectionStrategy, Component, OnDestroy, inject, output, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import type { GateEconomicsResponse, PipelineBaseline } from '../../../task-pipeline/models/gate-economics.model';
import { projectIdentity } from '../../../../services/project-identity.util';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** Develop and nightly-guard gate states onto the shared tones (AGT-3403); unknown values stay neutral. */
const GATE_STATE_TONE: Readonly<Record<string, StatusTone>> = { green: 'success', running: 'running', red: 'danger', skipped: 'neutral' };
export function gateStateTone(state: string | null | undefined): StatusTone {
  return state && Object.hasOwn(GATE_STATE_TONE, state) ? GATE_STATE_TONE[state] : 'neutral';
}

/** `lastNightlyGuard` reads "status · sha12"; the status is the first part. */
export function nightlyGuardTone(guard: string | null | undefined): StatusTone {
  return gateStateTone(guard?.split('·')[0].trim().toLowerCase());
}

@Component({
  selector: 'app-gate-economics-block',
  standalone: true,
  imports: [SegmentedControlComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './gate-economics-block.html',
  styleUrl: './gate-economics-block.scss',
})
export class GateEconomicsBlockComponent implements OnDestroy {
  readonly windowOptions = [{ value: '7d', label: '7 days' }, { value: '30d', label: '30 days' }] as const;
  readonly gateTone = gateStateTone;
  readonly nightlyTone = nightlyGuardTone;
  private readonly tasks = inject(TaskService);
  readonly data = signal<GateEconomicsResponse | null>(null);
  readonly window = signal<'7d' | '30d'>('7d');
  /** One fetch feeds the cross-project table and the per-project health row. */
  readonly loaded = output<GateEconomicsResponse>();
  private requestVersion = 0;
  private readonly poll = setInterval(() => this.refresh(), 60_000);

  constructor() { this.refresh(); }
  ngOnDestroy(): void { clearInterval(this.poll); this.requestVersion++; }
  setWindow(window: '7d' | '30d'): void { this.window.set(window); this.refresh(); }
  format(value: number | null, unit = ''): string {
    return value == null ? 'Unknown' : `${value.toLocaleString('en-US', { maximumFractionDigits: 1 })}${unit}`;
  }
  minutes(ms: number | null): string { return this.format(ms == null ? null : ms / 60_000, ' min'); }
  percent(value: number | null): string { return this.format(value == null ? null : value * 100, '%'); }
  /** KB below one MB, so a thin bundle never reads as 0 MB. */
  size(bytes: number | null): string {
    if (bytes == null) return this.format(null);
    return bytes < 1_048_576 ? this.format(bytes / 1024, ' KB') : this.format(bytes / 1_048_576, ' MB');
  }
  /** Red develop time needs the post-merge checks; until they record verdicts it is not measured. */
  redDevelop(baseline: PipelineBaseline | null | undefined): string {
    const median = baseline?.redDevelopMedianMinutes ?? null;
    return median === null ? 'Not measured'
      : `${this.format(median, ' min')} / ${this.format(baseline?.redDevelopP90Minutes ?? null, ' min')}`;
  }
  color(name: string): string { return projectIdentity(name).color; }
  private refresh(): void {
    const version = ++this.requestVersion;
    this.tasks.getGateEconomics(this.window()).subscribe({
      next: value => {
        if (version !== this.requestVersion || !Array.isArray(value?.rows)) return;
        this.data.set(value);
        this.loaded.emit(value);
      },
      error: () => { /* Preserve the last complete projection. */ },
    });
  }
}
