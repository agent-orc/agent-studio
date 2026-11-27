import { ChangeDetectionStrategy, Component, ElementRef, HostListener, OnDestroy, OnInit, ViewEncapsulation, inject, input, output } from '@angular/core';
import { StudioIconComponent } from '../studio-icon/studio-icon.component';
import { OverlayPortalRef, OverlayPortalService } from '../../services/overlay-portal.service';
import { ModalFocusDirective } from '../../directives/modal-focus.directive';
import { ModalStackService } from '../../services/modal-stack.service';

/**
 * Reusable modal dialog skeleton.
 *
 * The companion to <app-sidesheet>: this owns the **modal** shape
 * (centred panel, backdrop click-to-close, alertdialog ARIA, Esc
 * registration with ModalStackService, Tab containment and focus return
 * to the opener through `ModalFocusDirective`). Owners that already register
 * an Escape handler set `stackManagedExternally` to avoid a duplicate entry.
 * The two shapes are
 * deliberately separate components — they look similar but their
 * interaction semantics differ.
 *
 * Use this for: error, confirm, create-job, media-lightbox,
 * e2e-cleanup, update-block, update-center, verbose-debug,
 * orchestrator-settings — anything that asks the user to make a
 * one-off decision against the rest of the app.
 *
 * **Not** for side panels — those use <app-sidesheet>.
 *
 * Usage:
 *
 *   <app-dialog
 *     eyebrow="Confirm delete"
 *     title="Delete this task?"
 *     role="alertdialog"
 *     (close)="onCancel()"
 *     (backdropClick)="onCancel()">
 *     <p>This removes the job folder and all its files.</p>
 *     <div footer class="my-actions">
 *       <button (click)="onCancel()">Cancel</button>
 *       <button (click)="onConfirm()">Delete</button>
 *     </div>
 *   </app-dialog>
 *
 * Backdrop click is opt-in via the `(backdropClick)` event so callers
 * keep full control over cancellation semantics — some dialogs
 * (verbose-debug, log overlays) intentionally don't close on a
 * mis-click.
 */
@Component({
  selector: 'sui-dialog, app-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent, ModalFocusDirective],
  encapsulation: ViewEncapsulation.None,
  templateUrl: './dialog.component.html',
  styleUrl: './dialog.component.scss',
})
export class DialogComponent implements OnInit, OnDestroy {
  readonly eyebrow = input<string | null>(null);
  readonly title = input('');
  /** Optional one-line caption under the title. */
  readonly subtitle = input<string | null>(null);
  /** ARIA role — `alertdialog` for confirms/errors, `dialog` for forms. */
  readonly role = input<'dialog' | 'alertdialog'>('dialog');
  /** Optional exact width override (px). Wins over `widthTier`. Default 520. */
  readonly width = input<number | null>(null);
  /**
   * Responsive width tier. `md` (default) keeps the compact 520px panel for
   * confirm / error / form dialogs. `lg` switches to `--studio-modal-width-lg`
   * (`min(90vw, 880px)`) so content-heavy modals grow with the viewport and
   * shrink on small screens instead of stranding empty gutters. An explicit
   * `[width]` px override still takes precedence over the tier.
   */
  readonly widthTier = input<'md' | 'lg'>('md');
  /** Hides the close button when set to `false`. */
  readonly closable = input(true);
  /** Visual variant — drives accent stripe colour. */
  readonly kind = input<'default' | 'danger' | 'primary'>('default');
  /**
   * Body padding variant. `md` (default) uses --studio-modal-padding-body
   * (24px); `sm` drops to --studio-modal-padding-body-sm (16px) for confirm-
   * style dialogs that are intentionally tight. Header/footer always use
   * --studio-modal-padding-header / -footer regardless of size.
   */
  readonly size = input<'sm' | 'md'>('md');
  readonly testid = input<string | null>(null);
  /** Set when the owner already registers this dialog with ModalStackService. */
  readonly stackManagedExternally = input(false);

  /**
   * Relocate this dialog's host element to `<body>` on init.
   *
   * The overlay is `position: fixed`, but a fixed element is positioned
   * relative to the nearest ancestor that establishes a containing block
   * for fixed descendants — `transform`, `filter`, or `contain:
   * layout/paint` all do this. When the dialog renders inline inside such
   * an ancestor (e.g. the lane `.column`, which carries `contain: layout
   * paint` for scroll perf), the overlay is positioned against that tall,
   * scrolled box and then clipped off-screen by its `overflow: hidden`
   * parents instead of covering the viewport. Hosting at `<body>` escapes
   * both the containing block and the clipping. On by default because all
   * modal dialogs belong on the central overlay layer.
   */
  readonly portalToBody = input(true);

  private readonly host = inject(ElementRef).nativeElement as HTMLElement;
  private readonly overlayPortal = inject(OverlayPortalService);
  private readonly modalStack = inject(ModalStackService);
  private portalRef: OverlayPortalRef | null = null;
  private stackDispose: (() => void) | null = null;

  ngOnInit(): void {
    if (this.portalToBody()) {
      this.portalRef = this.overlayPortal.attachModal(this.host);
    }
    if (!this.stackManagedExternally()) {
      this.stackDispose = this.modalStack.push('app-dialog', () => this.closeRequest.emit());
    }
  }

  ngOnDestroy(): void {
    this.stackDispose?.();
    this.stackDispose = null;
    this.portalRef?.dispose();
    this.portalRef = null;
  }

  readonly closeRequest = output<void>();
  readonly backdropClick = output<void>();

  onBackdropClick(): void {
    this.backdropClick.emit();
  }

  /** Fallback for a dialog whose owner handles Escape outside the stack. */
  @HostListener('keydown.escape')
  onEscape(): void { this.closeRequest.emit(); }
}
