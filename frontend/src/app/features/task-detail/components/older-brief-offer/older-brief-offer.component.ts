import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { PendingButtonDirective } from '../../../../components/async-feedback';

type OlderBriefDecision = 'accept' | 'starting-point' | 'discard';

@Component({
  selector: 'app-older-brief-offer',
  standalone: true,
  imports: [PendingButtonDirective],
  templateUrl: './older-brief-offer.component.html',
  styleUrl: './older-brief-offer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OlderBriefOfferComponent {
  private readonly tasks = inject(TaskService);
  readonly task = input.required<TaskInfo>();
  readonly saved = output<void>();
  readonly failed = output<unknown>();
  readonly pending = signal<OlderBriefDecision | null>(null);

  decide(decision: OlderBriefDecision): void {
    if (this.pending() !== null) return;
    const task = this.task();
    this.pending.set(decision);
    this.tasks.decideOlderBriefDelivery(task.id, decision, task.watchPath).subscribe({
      next: () => {
        this.pending.set(null);
        this.saved.emit();
      },
      error: (error) => {
        this.pending.set(null);
        this.failed.emit(error);
      },
    });
  }
}
