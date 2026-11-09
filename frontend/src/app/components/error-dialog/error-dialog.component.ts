import { ChangeDetectionStrategy, Component, DestroyRef, inject, input, output, signal } from '@angular/core';
import { ErrorDialogState } from '../../models/error-dialog.model';
import { ModalStackService } from '../../services/modal-stack.service';
import { DialogComponent } from '../dialog/dialog.component';
import { DisclosureMarkerComponent } from '../disclosure-marker/disclosure-marker.component';

/**
 * Error overlay used by the global ErrorDialogService. The parent
 * watches `errorDialog.activeError()` and renders this component when
 * an error is present. An expected refusal (AGT-3624) renders its plain
 * sentence and next action, with the raw payload behind Technical details.
 */
@Component({
  selector: 'app-error-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DialogComponent, DisclosureMarkerComponent],
  templateUrl: './error-dialog.component.html',
  styleUrl: './error-dialog.component.scss',
})
export class ErrorDialogComponent {
  readonly error = input.required<ErrorDialogState>();
  readonly canOpenCliConfig = input(false);
  readonly copyButtonLabel = input<string>('Copy details');

  readonly closeRequest = output<void>();
  readonly copyRequest = output<void>();
  readonly openCliConfig = output<void>();

  /** Technical details of a refusal start collapsed. */
  readonly technicalOpen = signal(false);

  constructor() {
    // The error dialog is rendered only while open (`@if (errorDialog.activeError())`),
    // so a push-on-construct / dispose-on-destroy keeps the stack honest.
    // The previous implementation had no Escape handling at all - now Escape
    // routes through the central stack like every other modal.
    inject(ModalStackService).pushUntilDestroyed(
      'error-dialog',
      () => this.closeRequest.emit(),
      inject(DestroyRef),
    );
  }
}
