import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { TaskService } from '../../../../../services/task.service';
import type { PipelineStepProbeResult } from '../../../../task-pipeline';
import type { PipelineAdminRow } from '../pipeline-config.util';
import { StatusIndicatorComponent, type StatusTone } from '../../../../../components/status-indicator/status-indicator.component';

/** Probe result status onto the shared tones (AGT-3403); unknown values stay neutral. */
const PROBE_STATUS_TONE: Readonly<Record<string, StatusTone>> = {
  passed: 'success', failed: 'danger', skipped: 'neutral', 'not-applicable': 'neutral', unavailable: 'warning',
};

export function probeStatusTone(status: string): StatusTone {
  return Object.hasOwn(PROBE_STATUS_TONE, status) ? PROBE_STATUS_TONE[status] : 'neutral';
}

@Component({
  selector: 'app-pipeline-step-execution',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-step-execution.component.html',
  styleUrl: './pipeline-step-execution.component.scss',
})
export class PipelineStepExecutionComponent {
  readonly projectName = input.required<string>();
  readonly step = input.required<PipelineAdminRow>();
  readonly probeTone = probeStatusTone;
  private readonly taskService = inject(TaskService);

  readonly busy = signal(false);
  readonly result = signal<PipelineStepProbeResult | null>(null);
  readonly error = signal<string | null>(null);

  commandLabel(workingSubdir: string, command: string): string {
    return workingSubdir ? `cd ${workingSubdir} && ${command}` : command;
  }

  runProbe(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.result.set(null);
    const step = this.step();
    this.taskService.probePipelineStep(this.projectName(), step.id).subscribe({
      next: result => {
        this.result.set(result);
        this.busy.set(false);
      },
      error: error => {
        this.error.set(error?.error?.error ?? error?.message ?? 'Step probe failed.');
        this.busy.set(false);
      },
    });
  }
}
