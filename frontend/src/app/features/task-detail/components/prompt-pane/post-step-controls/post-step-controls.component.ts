import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { OnDemandPostStepAttempt, PostStepActivation } from '../../../../task-pipeline';
import { TaskPipelinePollService } from '../../../../polling/services/task-pipeline-poll.service';
import { StudioTabStateService } from '../../../../studio-shell/services/studio-tab-state.service';
import { PendingButtonDirective } from '../../../../../components/async-feedback';
import { NotificationService } from '../../../../../services/notification.service';
import { TaskService } from '../../../../../services/task.service';
import {
  PipelineStepResultComponent,
  type PipelineStepResultHeader,
} from '../pipeline-step-result/pipeline-step-result.component';
import { postStepAttemptTone } from '../pipeline-step-result/step-status-tone';
import { StatusIndicatorComponent } from '../../../../../components/status-indicator/status-indicator.component';

const SUPPORTED_STEP_IDS = new Set([
  'post-wiki-maintenance',
  'post-wiki-learnings',
  'post-agents-wiki-sync',
]);

@Component({
  selector: 'app-post-step-controls',
  standalone: true,
  imports: [TooltipDirective, PendingButtonDirective, PipelineStepResultComponent, StatusIndicatorComponent],
  templateUrl: './post-step-controls.component.html',
  styleUrl: './post-step-controls.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PostStepControlsComponent {
  readonly stepId = input.required<string>();
  readonly label = input.required<string>();
  readonly jobId = input.required<string>();
  readonly watchPath = input<string | undefined>();
  readonly projectName = input.required<string>();
  readonly activation = input<PostStepActivation | null>(null);
  readonly attempts = input<readonly OnDemandPostStepAttempt[]>([]);

  private readonly jobs = inject(TaskService);
  private readonly notifs = inject(NotificationService);
  private readonly pipelinePoll = inject(TaskPipelinePollService);
  private readonly studioTabs = inject(StudioTabStateService);

  readonly busy = signal(false);
  readonly historyOpen = signal(false);
  readonly supported = computed(() => SUPPORTED_STEP_IDS.has(this.stepId()));
  readonly activationView = computed(() => this.activation() ?? {
    state: 'unknown' as const,
    source: 'unknown' as const,
    reason: 'Activation metadata is unavailable from the backend.',
  });
  readonly activationScope = computed(() => {
    const activation = this.activationView();
    const code = activation.source === 'global'
      ? 'G'
      : activation.source === 'project'
        ? 'P'
        : activation.source === 'condition'
          ? 'C'
          : '?';
    return {
      code,
      description: `${activation.state} from ${activation.source}: ${activation.reason}`,
    };
  });
  readonly stepAttempts = computed(() => this.attempts()
    .filter(attempt => attempt.stepId === this.stepId())
    .sort((left, right) => right.attempt - left.attempt));
  readonly attemptCount = computed(() => this.stepAttempts().length);

  run(): void {
    if (!this.supported() || this.busy()) return;
    this.busy.set(true);
    this.jobs.runTaskPostStep(this.jobId(), this.stepId(), this.watchPath()).subscribe({
      next: result => {
        this.busy.set(false);
        this.pipelinePoll.refresh();
        this.notifs.success(
          `${this.label()} attempt #${result.attempt}: ${result.summary}`,
          'Post-step finished',
        );
      },
      error: () => {
        this.busy.set(false);
        this.notifs.warning(`${this.label()} could not be run.`, 'Post-step failed');
      },
    });
  }

  openPipelineSettings(): void {
    this.studioTabs.open({
      kind: 'hub',
      projectName: this.projectName(),
      section: 'pipeline',
      pipelineStepId: this.stepId(),
    });
  }

  readonly attemptTone = postStepAttemptTone;

  attemptHeader(attempt: OnDemandPostStepAttempt): PipelineStepResultHeader {
    const status = attempt.status.toLowerCase();
    const passed = status === 'ok' || status === 'warn';
    return {
      label: `${this.label()} attempt #${attempt.attempt}`,
      statusTone: postStepAttemptTone(status),
      statusLabel: attempt.status,
      status: passed ? 'passed' : status,
      verdict: null,
      model: null,
      durationLabel: `${attempt.durationMs} ms`,
      tokensLabel: null,
      costLabel: null,
    };
  }
}
