import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, effect, inject, input, model, output, untracked, viewChild } from '@angular/core';
import { ModalStackService } from '@studio/ui/services/modal-stack.service';

/**
 * A destructive action that is confirmed in place. At rest it is one quiet
 * danger button; pressed, it becomes a tinted strip that states the
 * consequence in one sentence and offers Cancel and the named action. Cancel
 * has focus first, and Escape cancels before it closes anything around it.
 *
 *   <app-inline-confirm label="Revoke" confirmLabel="Revoke token"
 *                       message="Scripts that use this token stop working at once."
 *                       (confirmed)="revoke(token)" />
 *
 * `armed` is two-way, so a row menu can open the confirmation from elsewhere.
 * Use it where the action has a detail area; an action without one keeps the
 * app-wide confirm dialog.
 */
@Component({
  selector: 'app-inline-confirm',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './inline-confirm.component.html',
  styleUrl: './inline-confirm.component.scss',
})
export class InlineConfirmComponent {
  /** The resting button, such as "Revoke" or "Disable user". */
  readonly label = input.required<string>();
  /** The confirming button; names the action and its object. Defaults to `label`. */
  readonly confirmLabel = input<string | null>(null);
  /** One sentence: what stops or is lost. */
  readonly message = input.required<string>();
  readonly disabled = input(false);
  readonly testId = input('inline-confirm');
  readonly armed = model(false);

  readonly confirmed = output<void>();

  private readonly modalStack = inject(ModalStackService);
  private readonly cancelButton = viewChild<ElementRef<HTMLButtonElement>>('cancelButton');
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');
  private releaseEscape: (() => void) | null = null;

  constructor() {
    effect(() => {
      const armed = this.armed();
      untracked(() => {
        if (armed) {
          this.releaseEscape ??= this.modalStack.push('inline-confirm', () => this.cancel());
          queueMicrotask(() => this.cancelButton()?.nativeElement.focus());
        } else {
          this.releaseEscape?.();
          this.releaseEscape = null;
        }
      });
    });
    inject(DestroyRef).onDestroy(() => this.releaseEscape?.());
  }

  cancel(): void {
    this.armed.set(false);
    queueMicrotask(() => this.trigger()?.nativeElement.focus());
  }

  confirm(): void {
    if (this.disabled()) return;
    this.armed.set(false);
    this.confirmed.emit();
  }
}
