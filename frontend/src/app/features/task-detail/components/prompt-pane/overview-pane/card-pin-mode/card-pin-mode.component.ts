import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { FormsModule, NgModel } from '@angular/forms';
import type { TaskInfo } from '../../../../../../models/task.model';
import { TaskService } from '../../../../../../services/task.service';

@Component({
  selector: 'app-card-pin-mode', standalone: true, changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule], templateUrl: './card-pin-mode.component.html', styleUrl: './card-pin-mode.component.scss',
})
export class CardPinModeComponent {
  private readonly modeControl = viewChild(NgModel);
  private readonly tasks = inject(TaskService);
  readonly job = input.required<TaskInfo>();
  readonly model = input<string | null>(null);
  readonly disabled = input(false);
  readonly saved = output<void>();
  readonly mode = signal<'preference' | 'hard'>('preference');
  readonly saving = signal(false);
  readonly error = signal('');

  constructor() { effect(() => this.mode.set(this.job().pinMode ?? 'preference')); }

  change(mode: 'preference' | 'hard'): void {
    if (this.disabled() || this.saving() || !['preference', 'hard'].includes(mode)) return;
    const job = this.job();
    const previous = this.mode();
    this.mode.set(mode); this.saving.set(true); this.error.set('');
    this.tasks.setJobModel(job.id, this.model() ?? job.model ?? null, job.watchPath, mode).subscribe({
      next: () => { this.saving.set(false); this.saved.emit(); },
      error: () => {
        this.mode.set(previous);
        this.modeControl()?.control.setValue(previous, { emitEvent: false, emitViewToModelChange: false });
        this.saving.set(false); this.error.set('Pin mode could not be saved.');
      },
    });
  }
}
