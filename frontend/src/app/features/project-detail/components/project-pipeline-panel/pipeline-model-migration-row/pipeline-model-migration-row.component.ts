import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output } from '@angular/core';
import { NotificationService } from '../../../../../services/notification.service';
import type { PipelineType } from '../../../../task-pipeline';
import { ModelMigrationOfferComponent, ModelMigrationService, type ModelMigrationProposal } from '../../../../model-migrations';

/** Project-step migration row, isolated from the already-large pipeline editor. */
@Component({
  selector: 'app-pipeline-model-migration-row',
  standalone: true,
  imports: [ModelMigrationOfferComponent],
  templateUrl: './pipeline-model-migration-row.component.html',
  styleUrl: './pipeline-model-migration-row.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PipelineModelMigrationRowComponent {
  readonly projectName = input.required<string>();
  readonly pipelineType = input.required<PipelineType>();
  readonly stepId = input.required<string>();
  readonly currentModel = input.required<string>();
  readonly applied = output<void>();
  private readonly migrations = inject(ModelMigrationService);
  private readonly notifications = inject(NotificationService);

  readonly proposal = computed(() => this.currentModel()
    ? this.migrations.proposalForPipelineStep(
        this.projectName(), this.pipelineType(), this.stepId(), this.currentModel(),
      )
    : null);

  constructor() {
    effect(() => {
      const projectName = this.projectName();
      if (projectName) this.migrations.ensureProjectLoaded(projectName);
    });
  }

  isBusy(proposal: ModelMigrationProposal): boolean {
    return this.migrations.isApplying(proposal);
  }

  apply(proposal: ModelMigrationProposal): void {
    if (this.migrations.isApplying(proposal)) return;
    this.migrations.apply(proposal).subscribe({
      next: () => {
        this.notifications.success(`Model updated to ${proposal.toModel}`);
        this.applied.emit();
      },
      error: () => this.notifications.error('Could not apply the pipeline model update.'),
    });
  }
}
