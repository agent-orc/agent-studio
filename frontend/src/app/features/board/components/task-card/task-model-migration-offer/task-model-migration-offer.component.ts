import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input } from '@angular/core';
import type { TaskInfo } from '../../../../../models/task.model';
import { NotificationService } from '../../../../../services/notification.service';
import { TaskService } from '../../../../../services/task.service';
import { ModelMigrationOfferComponent, ModelMigrationService, type ModelMigrationProposal } from '../../../../model-migrations';

/** Resolves and applies the explicit model-pin proposal for one board card. */
@Component({
  selector: 'app-task-model-migration-offer',
  standalone: true,
  imports: [ModelMigrationOfferComponent],
  templateUrl: './task-model-migration-offer.component.html',
  styleUrl: './task-model-migration-offer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskModelMigrationOfferComponent implements OnInit {
  readonly job = input.required<TaskInfo>();
  readonly blocked = input(false);
  private readonly migrations = inject(ModelMigrationService);
  private readonly notifications = inject(NotificationService);
  private readonly tasks = inject(TaskService);

  readonly proposal = computed(() => {
    const job = this.job();
    if (job.modelExplicit === false || !job.model) return null;
    return this.migrations.proposalForTask(job.id, job.projectName, job.model);
  });

  ngOnInit(): void {
    this.migrations.ensureWorkspaceLoaded();
  }

  isBusy(proposal: ModelMigrationProposal): boolean {
    return this.migrations.isApplying(proposal);
  }

  apply(proposal: ModelMigrationProposal): void {
    if (this.blocked() || this.migrations.isApplying(proposal)) return;
    this.migrations.apply(proposal).subscribe({
      next: () => {
        this.notifications.success(`Model updated to ${proposal.toModel}`);
        this.tasks.refresh(true);
      },
      error: () => this.notifications.error('Could not apply the model update.'),
    });
  }
}
