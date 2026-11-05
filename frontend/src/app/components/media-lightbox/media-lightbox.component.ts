import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  HostListener,
  effect,
  inject,
  signal,
} from '@angular/core';
import { MediaLightboxService } from '../../services/media-lightbox.service';
import { ModalStackService } from '../../services/modal-stack.service';
import { OverlayPortalDirective } from '../../directives/overlay-portal.directive';
import { TooltipDirective } from 'coding-agent-chat/shared';

/**
 * Single-instance image lightbox. Mounted on first use at the app shell so every
 * markdown surface (task description, activity-log, chat, info-button,
 * results) opens the same overlay through `MediaLightboxService`.
 *
 * Behaviour:
 *  - Backdrop click closes.
 *  - Close button closes.
 *  - Escape closes (via `ModalStackService` so a confirm dialog above the
 *    lightbox still wins).
 *  - Left/Right arrows page a gallery (Prev/Next). They are swallowed
 *    while the lightbox is open so they never reach the task-detail triage
 *    handler and switch the active task - see `onArrowKey`.
 *  - Large images are constrained to the viewport but allow zoom-to-actual
 *    via the click-on-image affordance (toggles `object-fit: contain`
 *    vs intrinsic size + scroll container).
 */
@Component({
  selector: 'app-media-lightbox',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, OverlayPortalDirective],
  templateUrl: './media-lightbox.component.html',
  styleUrl: './media-lightbox.component.scss',
})
export class MediaLightboxComponent {
  readonly lightbox = inject(MediaLightboxService);
  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);

  private stackDispose: (() => void) | null = null;

  /**
   * Original-size toggle. Off (default) fits the image into the fixed
   * stage (`object-fit: contain`, never upscaled); on shows it at its
   * intrinsic pixel size inside a scrollable stage. Reset on every image
   * change so paging always starts from the fitted view.
   */
  readonly zoomed = signal(false);

  constructor() {
    effect(() => {
      const open = this.lightbox.active() !== null;
      if (open) {
        if (!this.stackDispose) {
          this.stackDispose = this.modalStack.push('media-lightbox', () =>
            this.lightbox.close()
          );
        }
      } else if (this.stackDispose) {
        this.stackDispose();
        this.stackDispose = null;
      }
    });

    // Reset zoom on every image change and warm the neighbours so paging
    // with the arrows never shows a decode flash or reflows the stage.
    effect(() => {
      this.lightbox.active();
      this.lightbox.position();
      this.zoomed.set(false);
      preloadImage(this.lightbox.prevSrc());
      preloadImage(this.lightbox.nextSrc());
    });
    this.destroyRef.onDestroy(() => {
      if (this.stackDispose) {
        this.stackDispose();
        this.stackDispose = null;
      }
    });
  }

  /**
   * Page the gallery with the arrow keys.
   *
   * `<app-media-lightbox>` is mounted at the app shell, ahead of
   * `task-detail`, so this document-level listener fires before
   * `TaskDetailComponent.onTriageKey`. Calling `preventDefault()` makes
   * that handler bail (it returns early on `event.defaultPrevented`), so
   * the arrows page images instead of switching tasks. We swallow them
   * even for a single-image gallery (paging is a no-op there) so the keys
   * can never leak through to task navigation while a preview is open.
   *
   * Modifier chords are left alone: `onTriageKey` itself ignores
   * meta/ctrl/alt, so those never switch tasks and we don't need to eat
   * them here.
   */
  @HostListener('document:keydown', ['$event'])
  onArrowKey(event: KeyboardEvent): void {
    if (this.lightbox.active() === null) return;
    if (event.metaKey || event.ctrlKey || event.altKey) return;
    if (event.key === 'ArrowRight') {
      event.preventDefault();
      event.stopPropagation();
      this.lightbox.next();
    } else if (event.key === 'ArrowLeft') {
      event.preventDefault();
      event.stopPropagation();
      this.lightbox.prev();
    }
  }

  prev(): void {
    this.lightbox.prev();
  }

  next(): void {
    this.lightbox.next();
  }

  close(): void {
    this.lightbox.close();
  }

  /** Toggle intrinsic-size view; click on the image. */
  toggleZoom(event: Event): void {
    event.stopPropagation();
    this.zoomed.update((v) => !v);
  }

  async runAction(action: { run: () => void | Promise<void> }): Promise<void> {
    await action.run();
  }
}

/** Warm the browser cache for a neighbour image without rendering it. */
function preloadImage(src: string | null): void {
  if (!src) return;
  if (typeof Image === 'undefined') return;
  const img = new Image();
  img.decoding = 'async';
  img.src = src;
}
