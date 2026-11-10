import { afterNextRender, ChangeDetectionStrategy, Component, DestroyRef, inject, input, output } from '@angular/core';
import { ErrorDialogState } from '../../models/error-dialog.model';
import { ModalStackService } from '../../services/modal-stack.service';
import { DialogComponent } from '../dialog/dialog.component';

/**
 * Error overlay used by the global ErrorDialogService. The parent
 * watches `errorDialog.activeError()` and renders this component when
 * an error is present.
 */
@Component({
  selector: 'app-error-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DialogComponent],
  templateUrl: './error-dialog.component.html'
})
export class ErrorDialogComponent {
  readonly error = input.required<ErrorDialogState>();
  readonly canOpenCliConfig = input(false);
  readonly copyButtonLabel = input<string>('Copy details');

  readonly closeRequest = output<void>();
  readonly copyRequest = output<void>();
  readonly openCliConfig = output<void>();

  constructor() {
    const modalStack = inject(ModalStackService);
    const destroyRef = inject(DestroyRef);
    // Restoring the refused task can remount its detail after this component
    // is constructed. Register after that render so Escape reaches the error
    // dialog before the detail's own close handler.
    afterNextRender(() => {
      modalStack.pushUntilDestroyed('error-dialog', () => this.closeRequest.emit(), destroyRef);
      document.querySelector<HTMLElement>('[data-testid="error-dialog"]')?.focus();
    });
  }
}
