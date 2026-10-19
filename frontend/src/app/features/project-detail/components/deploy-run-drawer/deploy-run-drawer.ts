import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, computed, inject, input, output } from '@angular/core';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { SidesheetComponent } from '../../../../components/sidesheet/sidesheet.component';
import {
  OUTCOME_LABEL,
  evidenceLabel,
  formatDuration,
  shortArtifact,
  type DeployRun,
} from '../../state/deploy-area.model';

/** Detail drawer of one deploy run with its step log. Escape closes it. */
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
  readonly closeRequest = output<void>();

  readonly title = computed(() => `${this.run().kind === 'rollback' ? 'Rollback' : 'Deploy'} to ${this.run().environmentName}`);
  readonly duration = computed(() => formatDuration(this.run().queuedAt, this.run().finishedAt));
  readonly evidence = computed(() => evidenceLabel(this.run().evidence));

  protected readonly outcomeLabel = OUTCOME_LABEL;
  protected readonly shortArtifact = shortArtifact;

  constructor() {
    // Escape goes through the shared modal stack, so the drawer closes before any surface below it.
    inject(ModalStackService).pushUntilDestroyed('deploy-run-drawer', () => this.closeRequest.emit(), inject(DestroyRef));
    // Move focus into the drawer so keyboard users land on its controls.
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('[data-testid="sidesheet-close"]')?.focus());
  }
}
