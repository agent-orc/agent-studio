import { Directive, ElementRef, InjectionToken, OnDestroy, inject, input } from '@angular/core';

export interface TooltipRenderer {
  show(anchor: HTMLElement, content: string, position: 'auto' | 'top' | 'bottom' | 'left' | 'right', severity?: 'info' | 'warn' | 'error' | 'success'): void;
  hide(anchor: HTMLElement): void;
}

// The host supplies its existing renderer so moving a control does not change
// tooltip chrome. The library never imports a host or chat implementation.
export const STUDIO_TOOLTIP_RENDERER = new InjectionToken<TooltipRenderer>('Studio tooltip renderer', {
  providedIn: 'root', factory: () => ({
    show: (anchor, content) => anchor.setAttribute('title', content),
    hide: anchor => anchor.removeAttribute('title'),
  }),
});

@Directive({
  selector: '[cacTooltip]',
  standalone: true,
  host: {
    '(mouseenter)': 'show()', '(mouseleave)': 'hide()',
    '(focusin)': 'show()', '(focusout)': 'hide()', '(click)': 'hide()',
    '(touchstart)': 'onTouchStart()', '(document:touchstart)': 'onDocumentTouch($event)',
  },
})
export class LegacyTooltipDirective implements OnDestroy {
  readonly content = input('', { alias: 'cacTooltip' });
  readonly tooltipPosition = input<'auto' | 'top' | 'bottom' | 'left' | 'right'>('auto');
  readonly tooltipSeverity = input<'info' | 'warn' | 'error' | 'success'>();
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly renderer = inject(STUDIO_TOOLTIP_RENDERER);
  private touchTimer: ReturnType<typeof setTimeout> | null = null;

  show(): void {
    if (this.content()) this.renderer.show(this.host, this.content(), this.tooltipPosition(), this.tooltipSeverity());
  }
  hide(): void { this.renderer.hide(this.host); }
  onTouchStart(): void {
    this.show();
    this.clearTimer();
    this.touchTimer = setTimeout(() => { this.hide(); this.touchTimer = null; }, 3000);
  }
  onDocumentTouch(event: TouchEvent): void {
    if (this.touchTimer && !this.host.contains(event.target as Node)) { this.clearTimer(); this.hide(); }
  }
  ngOnDestroy(): void { this.clearTimer(); this.hide(); }
  private clearTimer(): void {
    if (this.touchTimer) clearTimeout(this.touchTimer);
    this.touchTimer = null;
  }
}
