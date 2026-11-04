import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type {
  PublishActionPanel,
  PublishAutomationMode,
  PublishTarget,
  PublishWorkflowRun,
} from '../../../../models/task.model';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { TaskService } from '../../../../services/task.service';
import { TooltipDirective } from 'coding-agent-chat/shared';

const RUNNING_STATUSES = new Set(['queued', 'in_progress', 'waiting', 'requested', 'pending']);

const CONCLUSION_TONES: Record<string, StatusTone> = {
  success: 'success',
  failure: 'danger',
  timed_out: 'danger',
  startup_failure: 'danger',
  action_required: 'warning',
  cancelled: 'neutral',
  skipped: 'neutral',
  neutral: 'neutral',
  stale: 'neutral',
};

/**
 * Raw GitHub workflow status and conclusion as one indicator: a queued or
 * running workflow is in progress; a completed one takes the conclusion tone.
 */
export function publishRunStatus(run: Pick<PublishWorkflowRun, 'status' | 'conclusion'>): { label: string; tone: StatusTone } {
  const label = run.conclusion ? `${run.status} · ${run.conclusion}` : run.status;
  const status = (run.status ?? '').toLowerCase();
  if (RUNNING_STATUSES.has(status)) return { label, tone: 'running' };
  if (status === 'completed') return { label, tone: CONCLUSION_TONES[(run.conclusion ?? '').toLowerCase()] ?? 'neutral' };
  return { label, tone: 'neutral' };
}

@Component({
  selector: 'app-project-publish-panel',
  standalone: true,
  imports: [FormsModule, TooltipDirective, StatusIndicatorComponent],
  templateUrl: './project-publish-panel.html',
  styleUrl: './project-publish-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectPublishPanelComponent {
  readonly runStatus = publishRunStatus;
  readonly projectName = input.required<string>();
  readonly targets = input.required<readonly PublishTarget[]>();

  private readonly tasks = inject(TaskService);
  readonly selected = signal<PublishActionPanel | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly run = signal<PublishWorkflowRun | null>(null);
  versionDraft = '';
  automationDraft: PublishAutomationMode = 'manual';

  readonly badges = computed(() => this.targets()
    .filter(target => target.firstPublishPending || (target.pendingCount ?? 0) > 0)
    .map(target => ({
      target,
      text: target.firstPublishPending
        ? `${target.label} first publish pending`
        : `${target.currentVersion ? `${target.label} ${target.currentVersion}` : target.label} → ${target.pendingCount} task${target.pendingCount === 1 ? '' : 's'} pending`,
      tooltip: target.firstPublishPending
        ? `${target.label} has never been published. First publish is a manual operator action.`
        : `Open the guided ${target.kind === 'package' ? 'release' : 'deployment'} flow.`,
    })));

  open(target: PublishTarget): void {
    this.busy.set(true);
    this.message.set(null);
    this.tasks.getPublishPanel(this.projectName(), target.id).subscribe({
      next: panel => {
        this.selected.set(panel);
        this.versionDraft = panel.suggestedVersion ?? '';
        this.automationDraft = panel.automationMode;
        this.run.set(panel.lastRun);
        this.busy.set(false);
      },
      error: error => this.fail(error, 'Could not open the publish flow.'),
    });
  }

  close(): void {
    this.selected.set(null);
    this.message.set(null);
  }

  saveAutomation(): void {
    const target = this.selected()?.target;
    if (!target) return;
    this.busy.set(true);
    this.tasks.setPublishAutomation(this.projectName(), target.id, this.automationDraft).subscribe({
      next: result => {
        this.automationDraft = result.mode;
        this.message.set(`Automation set to ${result.mode}.`);
        this.busy.set(false);
      },
      error: error => this.fail(error, 'Could not save automation.'),
    });
  }

  trigger(): void {
    const panel = this.selected();
    if (!panel || panel.notice) return;
    this.busy.set(true);
    this.message.set(null);
    const request = panel.target.kind === 'package'
      ? this.tasks.publishPackage(this.projectName(), panel.target.id, this.versionDraft.trim())
      : this.tasks.deployWebsite(this.projectName());
    request.subscribe({
      next: run => {
        this.run.set(run);
        this.message.set(panel.target.kind === 'package'
          ? `v${run.version} pushed. The existing release workflow is now tracking.`
          : 'Website workflow dispatched.');
        this.busy.set(false);
      },
      error: error => this.fail(error, 'Publish action failed.'),
    });
  }

  refreshRun(): void {
    const target = this.selected()?.target;
    if (!target) return;
    this.busy.set(true);
    this.tasks.getPublishRun(this.projectName(), target.id).subscribe({
      next: run => { this.run.set(run); this.busy.set(false); },
      error: error => this.fail(error, 'Could not refresh workflow status.'),
    });
  }

  private fail(error: unknown, fallback: string): void {
    const response = error as { error?: { error?: string }; message?: string } | null;
    this.message.set(response?.error?.error ?? response?.message ?? fallback);
    this.busy.set(false);
  }
}
