import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import type { VisibleCliTaskCreated, VisibleCliTaskRequest, VisibleCliTaskWorkspace } from '../../models/visible-cli-task.model';
import { VisibleCliTaskService } from '../../services/visible-cli-task.service';

@Component({
  selector: 'app-visible-cli-task-card',
  standalone: true,
  imports: [FormsModule, StatusIndicatorComponent],
  templateUrl: './visible-cli-task-card.html',
  styleUrl: './visible-cli-task-card.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VisibleCliTaskCardComponent {
  private readonly starter = inject(VisibleCliTaskService);
  readonly request = input.required<VisibleCliTaskRequest>();
  readonly workspaces = input<readonly VisibleCliTaskWorkspace[]>([]);
  readonly taskCreated = output<VisibleCliTaskCreated>();
  readonly selectedWatchPath = signal('');
  readonly running = signal(false);
  readonly error = signal<string | null>(null);
  readonly created = signal<VisibleCliTaskCreated | null>(null);

  constructor() {
    effect(() => {
      const workspaces = this.workspaces();
      if (!workspaces.some(item => item.path === this.selectedWatchPath())) {
        this.selectedWatchPath.set(workspaces[0]?.path ?? '');
      }
    });
  }

  start(): void {
    const watchPath = this.selectedWatchPath();
    if (!watchPath || this.running()) return;
    this.running.set(true);
    this.error.set(null);
    this.starter.start(this.request(), watchPath).subscribe({
      next: task => {
        this.created.set(task);
        this.taskCreated.emit(task);
      },
      error: err => {
        this.error.set(err?.error?.message ?? err?.error ?? 'The CLI task could not be created.');
        this.running.set(false);
      },
      complete: () => this.running.set(false),
    });
  }

  openTask(): void {
    const task = this.created();
    if (task) this.taskCreated.emit(task);
  }
}
