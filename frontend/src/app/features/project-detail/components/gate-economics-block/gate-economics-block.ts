import { ChangeDetectionStrategy, Component, OnDestroy, inject, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import type { GateEconomicsResponse } from '../../../task-pipeline/models/gate-economics.model';
import { projectIdentity } from '../../../../services/project-identity.util';

@Component({
  selector: 'app-gate-economics-block',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './gate-economics-block.html',
  styleUrl: './gate-economics-block.scss',
})
export class GateEconomicsBlockComponent implements OnDestroy {
  private readonly tasks = inject(TaskService);
  readonly data = signal<GateEconomicsResponse | null>(null);
  readonly window = signal<'7d' | '30d'>('7d');
  private readonly poll = setInterval(() => this.refresh(), 60_000);

  constructor() { this.refresh(); }
  ngOnDestroy(): void { clearInterval(this.poll); }
  setWindow(window: '7d' | '30d'): void { this.window.set(window); this.refresh(); }
  format(value: number | null, unit = ''): string {
    return value == null ? 'Unknown' : `${value.toLocaleString('en-US', { maximumFractionDigits: 1 })}${unit}`;
  }
  percent(value: number | null): string { return this.format(value == null ? null : value * 100, '%'); }
  color(name: string): string { return projectIdentity(name).color; }
  private refresh(): void {
    this.tasks.getGateEconomics(this.window()).subscribe({
      next: value => this.data.set(value),
      error: () => { /* Preserve the last complete projection. */ },
    });
  }
}
