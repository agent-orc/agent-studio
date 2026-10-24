import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { ProductRuntimeService, TaskDetailLatency } from '../../../../services/product-runtime.service';

/** Recent card response times from the current backend process. */
@Component({
  selector: 'app-task-detail-latency',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-detail-latency.component.html',
  styleUrl: './task-detail-latency.component.scss',
})
export class TaskDetailLatencyComponent implements OnInit, OnDestroy {
  private readonly runtime = inject(ProductRuntimeService);
  readonly metric = signal<TaskDetailLatency | null>(null);
  readonly Math = Math;
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.refresh();
    this.pollTimer = setInterval(() => this.refresh(), 15_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  private refresh(): void {
    this.runtime.getTaskDetailLatency().subscribe({
      next: value => this.metric.set(value),
      error: () => this.metric.set(null),
    });
  }
}
