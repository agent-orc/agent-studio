import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { ModelLevelIndicatorComponent } from '../../../../components/model-level-indicator/model-level-indicator.component';
import { ModelMigrationOfferComponent } from '../../../../components/model-migration-offer';
import type { TaskInfo } from '../../../../models/task.model';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';
import type { EffectiveModelChip } from '../task-card/task-card-view-model';

@Component({
  selector: 'app-task-model-migration-control',
  standalone: true,
  imports: [ModelLevelIndicatorComponent, ModelMigrationOfferComponent],
  templateUrl: './task-model-migration-control.component.html',
  styleUrl: './task-model-migration-control.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskModelMigrationControlComponent {
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  readonly task = input.required<TaskInfo>();
  readonly chip = input.required<EffectiveModelChip>();
  readonly mutationsBlocked = input(false);
  readonly applying = signal(false);

  applyMigration(): void {
    const task = this.task();
    const proposal = task.modelMigration;
    if (!proposal || this.applying() || this.mutationsBlocked()) return;

    this.applying.set(true);
    this.tasks.setJobModel(task.id, proposal.to, task.watchPath).subscribe({
      next: () => {
        this.applying.set(false);
        this.notifications.success(`Model updated to ${proposal.to}`);
        this.tasks.refresh(true);
      },
      error: () => {
        this.applying.set(false);
        this.notifications.error(`Could not update the model to ${proposal.to}`);
      },
    });
  }
}
