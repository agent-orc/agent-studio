import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, computed, inject, input, output } from '@angular/core';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { SidesheetComponent } from '../../../../components/sidesheet/sidesheet.component';
import {
  RUN_STATE_LABEL,
  STEP_STATE_LABEL,
  formatDuration,
  groupStepLog,
  stepKindLabel,
  runActions,
  triggerLabel,
  type DeployPanel,
  type DeployRun,
  type DeployRunAction,
  type DeploymentLogLine,
} from '../../state/deploy-area.model';

export interface DeployRunActionState {
  busy: DeployRunAction | null;
  error: string | null;
}

const ACTION_LABEL: Record<DeployRunAction, string> = { approve: 'Approve', reject: 'Reject', cancel: 'Abort run' };

/** Detail drawer of one AGT-3201 deploy run: facts, approval, steps with their log. Escape closes it. */
@Component({
  selector: 'app-deploy-run-drawer',
  standalone: true,
  imports: [DatePipe, SidesheetComponent],
  templateUrl: './deploy-run-drawer.html',
  styleUrl: './deploy-run-drawer.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DeployRunDrawerComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly run = input.required<DeployRun>();
  readonly log = input.required<DeployPanel<DeploymentLogLine[]>>();
  readonly action = input<DeployRunActionState>({ busy: null, error: null });
  readonly act = output<DeployRunAction>();
  readonly closeRequest = output<void>();

  readonly title = computed(() => `${triggerLabel(this.run().trigger)} to ${this.run().environmentName}`);
  readonly subtitle = computed(() => this.run().releaseVersion ? `Release ${this.run().releaseVersion}` : this.run().releaseId);
  readonly duration = computed(() => formatDuration(this.run().startedAt, this.run().finishedAt));
  readonly approval = computed(() => {
    const run = this.run();
    if (run.approvedBy) return `Approved by ${run.approvedBy}`;
    if (run.state === 'awaiting-approval') return 'Waiting for an Owner or Maintainer';
    return run.steps.some(step => step.name === 'approval') ? 'Not approved' : 'Not required';
  });
  readonly actions = computed(() => runActions(this.run().state).map(kind => ({ kind, label: ACTION_LABEL[kind] })));
  readonly logError = computed(() => {
    const log = this.log();
    return log.status === 'error' ? log.message : null;
  });
  readonly steps = computed(() => {
    const log = this.log();
    return groupStepLog(this.run().steps, log.status === 'ready' ? log.value : []);
  });

  protected readonly stateLabel = RUN_STATE_LABEL;
  protected readonly stepLabel = STEP_STATE_LABEL;
  protected readonly stepKindLabel = stepKindLabel;

  constructor() {
    // Escape goes through the shared modal stack, so the drawer closes before any surface below it.
    inject(ModalStackService).pushUntilDestroyed('deploy-run-drawer', () => this.closeRequest.emit(), inject(DestroyRef));
    // Move focus into the drawer so keyboard users land on its controls.
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('[data-testid="sidesheet-close"]')?.focus());
  }
}
