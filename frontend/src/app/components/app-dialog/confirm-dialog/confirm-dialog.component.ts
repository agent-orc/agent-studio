import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  ViewChild,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { ConfirmDialogService } from '../../../services/confirm-dialog.service';
import { ModalStackService } from '../../../services/modal-stack.service';
import { DialogComponent } from '../../dialog/dialog.component';

/**
 * App-wide confirm dialog. Mounted once at the shell; visibility driven by
 * `ConfirmDialogService.active()`. Renders a modal with the same panel,
 * eyebrow, typography, and footer pattern as the error overlay so every
 * confirmation feels native to the dark Catppuccin-inspired UI.
 *
 * Behaviour:
 *  - Esc cancels.
 *  - Enter confirms (primary action).
 *  - Backdrop click cancels.
 *  - Focus is moved to the primary button when the dialog opens and the
 *    Tab key is trapped between the two action buttons + close affordance.
 *
 * Errors with stack-trace / copy remain on `ErrorDialogService` — those
 * carry strictly more information and rely on the dedicated overlay.
 */
@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DialogComponent],
  templateUrl: './confirm-dialog.component.html',
})
export class ConfirmDialogComponent {
  private readonly service = inject(ConfirmDialogService);
  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);

  readonly state = this.service.active;
  readonly hasOpen = computed(() => this.state() !== null);

  /** Live value of the type-to-confirm input (empty when not required). */
  readonly typedDraft = signal('');

  /**
   * True when the dialog has no type-to-confirm gate, or the trimmed input
   * matches one of the required values (case-insensitive). Drives both the
   * confirm button's disabled state and the Enter-to-confirm guard so a
   * destructive delete cannot complete until the operator re-types the
   * project name or short code.
   */
  readonly typedConfirmSatisfied = computed(() => {
    const s = this.state();
    const required = s?.requireTypedValues;
    if (!required || required.length === 0) return true;
    const typed = this.typedDraft().trim().toLowerCase();
    if (typed.length === 0) return false;
    return required.some(v => v.trim().toLowerCase() === typed);
  });

  @ViewChild('confirmBtn') private confirmBtnRef?: ElementRef<HTMLButtonElement>;
  @ViewChild('typedInput') private typedInputRef?: ElementRef<HTMLInputElement>;

  /** Tracks the previously-focused element so we can restore focus on close. */
  private previousFocus = signal<HTMLElement | null>(null);
  private modalStackDispose: (() => void) | null = null;

  constructor() {
    effect(() => {
      const open = this.hasOpen();
      if (open) {
        const active = (document.activeElement as HTMLElement | null) ?? null;
        this.previousFocus.set(active);
        // Reset the type-to-confirm input for each fresh prompt, then focus
        // it when present (so the operator can type immediately); otherwise
        // focus the confirm button as before.
        this.typedDraft.set('');
        queueMicrotask(() => {
          const input = this.typedInputRef?.nativeElement;
          if (input) input.focus();
          else this.confirmBtnRef?.nativeElement.focus();
        });
        if (!this.modalStackDispose) {
          this.modalStackDispose = this.modalStack.push('confirm-dialog', () => this.service.cancel());
        }
      } else {
        const prev = this.previousFocus();
        this.previousFocus.set(null);
        if (prev && typeof prev.focus === 'function') {
          try { prev.focus(); } catch { /* element gone */ }
        }
        if (this.modalStackDispose) {
          this.modalStackDispose();
          this.modalStackDispose = null;
        }
      }
    });
    this.destroyRef.onDestroy(() => {
      if (this.modalStackDispose) {
        this.modalStackDispose();
        this.modalStackDispose = null;
      }
    });
  }

  onAccept(): void {
    if (!this.typedConfirmSatisfied()) return;
    this.service.accept();
  }

  onCancel(): void {
    this.service.cancel();
  }

  onBackdropClick(): void {
    this.service.cancel();
  }

  /** R8: the item list owns its vertical scroll keys while it has focus. */
  containListKeys(event: KeyboardEvent): void {
    const list = event.currentTarget as HTMLElement;
    const moves: Record<string, number> = {
      ArrowUp: -32, ArrowDown: 32, PageUp: -list.clientHeight, PageDown: list.clientHeight,
      Home: -list.scrollHeight, End: list.scrollHeight,
    };
    if (!(event.key in moves)) return;
    event.preventDefault();
    event.stopPropagation();
    list.scrollBy({ top: moves[event.key] });
  }

  /**
   * Enter confirms when the focus is on the dialog. We avoid hijacking
   * Enter globally — the global handler is scoped to keydown originating
   * inside the dialog's panel via the template.
   */
  onPanelKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Enter') return;
    const target = event.target as HTMLElement | null;
    if (target?.tagName === 'TEXTAREA') return;
    event.preventDefault();
    event.stopPropagation();
    // A type-to-confirm gate must be satisfied before Enter completes the
    // prompt — otherwise the operator could blow past the safety input.
    if (!this.typedConfirmSatisfied()) return;
    this.service.accept();
  }
}
