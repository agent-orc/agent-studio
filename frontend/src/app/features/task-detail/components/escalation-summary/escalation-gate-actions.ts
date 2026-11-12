import { signal } from '@angular/core';
import type { BatchGateService } from '../../../../services/batch-gate.service';
import type { TaskInfo } from '../../../../models/task.model';

/** Retry-gate and disable-batch actions for a gate-infrastructure park (AGT-3708). */
export class EscalationGateActions {
  readonly action = signal<'retry' | 'disable' | null>(null);
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  constructor(
    private readonly batchGate: BatchGateService,
    private readonly info: () => TaskInfo,
    private readonly blocked: () => boolean,
  ) {}

  retry(): void {
    if (this.action() || this.blocked()) return;
    const info = this.info();
    this.action.set('retry');
    this.error.set(null);
    this.batchGate.retryInfrastructureGate(info.projectName, info.key ?? info.id).subscribe({
      next: () => {
        this.action.set(null);
        this.message.set('The reviewed delivery returned to verification.');
      },
      error: err => {
        this.action.set(null);
        this.error.set(err?.error?.error ?? 'The gate retry failed. Check the remote gate host and try again.');
      },
    });
  }

  disable(): void {
    if (this.action() || this.blocked()) return;
    const project = this.info().projectName;
    this.action.set('disable');
    this.error.set(null);
    const failed = (err: { error?: { error?: string } }) => {
      this.action.set(null);
      this.error.set(err?.error?.error ?? 'Could not disable batch verification.');
    };
    this.batchGate.getSettings(project).subscribe({
      next: current => this.batchGate.putSettings(project, { ...current.settings, enabled: false }).subscribe({
        next: () => {
          this.action.set(null);
          this.message.set('Batch verification is off for this project. This card stays parked. Re-enable batching after restoring the gate host, then retry its gate.');
        },
        error: failed,
      }),
      error: failed,
    });
  }
}
