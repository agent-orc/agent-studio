import {
  ChangeDetectionStrategy,
  Component,
  inject,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import { NotificationService } from '@studio/ui/services/notification.service';
import { TaskService } from '../../../../../services/task.service';
import type { PipelineStepConfig } from '../../../../task-pipeline';

/**
 * Compact project-level enable switch for an optional pipeline step that has
 * not run yet. The pipeline-step endpoint replaces the complete override, so
 * every unchanged config facet is sent back alongside the new enabled value.
 */
@Component({
  selector: 'app-pipeline-step-toggle',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-step-toggle.component.html',
  styleUrl: './pipeline-step-toggle.component.scss',
})
export class PipelineStepToggleComponent {
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);

  readonly projectName = input.required<string>();
  readonly stepId = input.required<string>();
  readonly label = input.required<string>();
  readonly enabled = input.required<boolean>();
  readonly config = input<PipelineStepConfig | null>(null);

  readonly changed = output<boolean>();
  readonly busy = signal(false);
  readonly optimisticEnabled = linkedSignal(() => this.enabled());

  toggle(event?: Event): void {
    event?.stopPropagation();
    if (this.busy()) return;

    const previous = this.optimisticEnabled();
    const next = !previous;
    const config = this.config();

    this.optimisticEnabled.set(next);
    this.busy.set(true);

    this.tasks.setProjectPipelineStep(this.projectName(), {
      stepId: this.stepId(),
      enabled: next,
      cliType: config?.cliType ?? null,
      model: config?.model ?? null,
      thinkingLevel: config?.thinkingLevel ?? null,
      mode: config?.mode ?? null,
      prompt: config?.prompt ?? null,
      condition: config?.condition ?? null,
    }).subscribe({
      next: () => {
        this.busy.set(false);
        this.changed.emit(next);
      },
      error: () => {
        this.optimisticEnabled.set(previous);
        this.busy.set(false);
        this.notifications.warning(
          `${this.label()} could not be ${next ? 'enabled' : 'disabled'}. Try again in a moment.`,
          'Pipeline step update failed',
        );
      },
    });
  }
}
