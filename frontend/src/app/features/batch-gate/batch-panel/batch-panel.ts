import { ChangeDetectionStrategy, Component, HostListener, computed, effect, inject, input, signal } from '@angular/core';
import { BatchGateService } from '../../../services/batch-gate.service';

@Component({
  selector: 'app-batch-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-panel.html',
  styleUrl: './batch-panel.scss',
})
export class BatchPanelComponent {
  readonly project = input.required<string>();
  readonly closed = input.required<() => void>();
  readonly gate = inject(BatchGateService);
  readonly state = computed(() => this.gate.state(this.project())());
  readonly error = computed(() => this.gate.error(this.project())());
  readonly confirm = signal<string | null>(null);
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly phases = ['forming', 'replaying', 'suite-running', 'halving', 'publishing', 'published', 'abandoned'];

  constructor() {
    effect(onCleanup => {
      const project = this.project();
      if (project) onCleanup(this.gate.watch(project));
    });
  }

  date(value?: string): string {
    if (!value) return 'Unknown';
    return new Date(value).toLocaleString('en-US', { dateStyle: 'medium', timeStyle: 'short' });
  }
  duration(start?: string, end?: string): string {
    if (!start) return 'Waiting';
    if (!end) return 'Running';
    const minutes = Math.max(0, Math.round((Date.parse(end) - Date.parse(start)) / 60_000));
    return `${minutes} min`;
  }
  phaseLabel(phase: string): string { return phase.replace('-', ' '); }
  close(): void { this.closed()(); }
  @HostListener('document:keydown.escape') closeOnEscape(): void { this.close(); }
  act(): void {
    const target = this.confirm();
    if (!target || this.busy()) return;
    this.busy.set(true);
    this.actionError.set(null);
    const request = target === 'close' ? this.gate.closeNow(this.project()) : this.gate.eject(this.project(), target);
    request.subscribe({
      next: () => { this.busy.set(false); this.confirm.set(null); void this.gate.refresh(this.project()); },
      error: err => { this.busy.set(false); this.actionError.set(err?.error?.error ?? 'The action could not be completed.'); },
    });
  }
}
