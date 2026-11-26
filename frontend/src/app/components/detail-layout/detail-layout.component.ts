import {
  ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, effect, inject, input, output, signal, untracked, viewChild,
} from '@angular/core';
import { ModalStackService } from '../../services/modal-stack.service';
import { SidesheetComponent } from '../sidesheet/sidesheet.component';

/**
 * Master and detail on one page: the main column with its grids, and one
 * detail area for the selected row. On a wide layout the detail is a side
 * panel that pushes the main column and stays in view while the page scrolls;
 * below 760 px of the layout's own width it covers the page as a sheet.
 * The panel chrome is the shared `<app-sidesheet>`.
 *
 *   <app-detail-layout [open]="!!selected()" eyebrow="Service account" [title]="name()"
 *                      [contextKey]="selected()?.id" [footer]="true" (closeRequest)="clear()">
 *     ...page sections...
 *     <div detail>...facts and actions of the selected row...</div>
 *     <div detailFooter>...destructive actions, set apart...</div>
 *   </app-detail-layout>
 *
 * The layout sets `--detail-layout-inset` to the top padding of the scrolling
 * ancestor; a sticky bar in the main column uses its negative as `top` to sit
 * flush with the edge of the page.
 *
 * Opening moves focus into the detail; closing (button, Escape, scrim)
 * returns it to the control that opened it.
 */
@Component({
  selector: 'app-detail-layout',
  standalone: true,
  imports: [SidesheetComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './detail-layout.component.html',
  styleUrl: './detail-layout.component.scss',
})
export class DetailLayoutComponent {
  readonly open = input(false);
  readonly eyebrow = input<string | null>(null);
  readonly title = input('');
  readonly subtitle = input<string | null>(null);
  /** Changes when another row is selected while the detail stays open; focus then moves into the detail again. */
  readonly contextKey = input<string | null | undefined>(null);
  /** True when the detail has set-apart actions; the footer strip renders only then. */
  readonly footer = input(false);
  readonly testId = input('detail-layout');

  readonly closeRequest = output<void>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly modalStack = inject(ModalStackService);
  private readonly panel = viewChild<ElementRef<HTMLElement>>('panel');
  /** Visible height of the scrolling ancestor and its top padding, so the side panel fills the visible page. */
  readonly viewport = signal<{ height: number; inset: number } | null>(null);
  private opener: HTMLElement | null = null;
  private releaseEscape: (() => void) | null = null;
  private resize: ResizeObserver | null = null;

  constructor() {
    effect(() => {
      const open = this.open();
      this.contextKey();
      untracked(() => (open ? this.onOpen() : this.onClose()));
    });
    afterNextRender(() => this.watchViewport());
    inject(DestroyRef).onDestroy(() => {
      this.releaseEscape?.();
      this.resize?.disconnect();
    });
  }

  private onOpen(): void {
    const active = typeof document === 'undefined' ? null : document.activeElement;
    if (active instanceof HTMLElement && !this.panel()?.nativeElement.contains(active)) this.opener = active;
    this.releaseEscape ??= this.modalStack.push('detail-layout', () => this.closeRequest.emit());
    this.measure();
    // The panel renders after this effect; focus it once it exists.
    queueMicrotask(() => this.panel()?.nativeElement.focus({ preventScroll: true }));
  }

  private onClose(): void {
    this.releaseEscape?.();
    this.releaseEscape = null;
    const opener = this.opener;
    this.opener = null;
    if (opener?.isConnected) opener.focus({ preventScroll: true });
  }

  private watchViewport(): void {
    if (typeof ResizeObserver === 'undefined') return;
    const scroller = this.scroller();
    if (!scroller) return;
    this.resize = new ResizeObserver(() => this.measure());
    this.resize.observe(scroller);
    this.measure();
  }

  private measure(): void {
    const scroller = this.scroller();
    if (!scroller) return;
    // A sticky box lives inside the padding of its scroller, so the visible page is the client height less both paddings.
    const style = getComputedStyle(scroller);
    const top = parseFloat(style.paddingTop) || 0;
    const bottom = parseFloat(style.paddingBottom) || 0;
    this.viewport.set({ height: Math.max(0, scroller.clientHeight - top - bottom), inset: top });
  }

  /** The nearest ancestor that scrolls vertically; the page keeps one scroll owner. */
  private scroller(): HTMLElement | null {
    for (let node = this.host.nativeElement.parentElement; node; node = node.parentElement) {
      const overflow = getComputedStyle(node).overflowY;
      if (overflow === 'auto' || overflow === 'scroll') return node;
    }
    return null;
  }
}
