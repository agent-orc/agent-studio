import { DOCUMENT } from '@angular/common';
import { Directive, ElementRef, OnDestroy, inject, input } from '@angular/core';

let nextTooltipId = 0;

@Directive({
  selector: '[appTooltip]',
  standalone: true,
  host: {
    '(mouseenter)': 'scheduleShow()',
    '(mouseleave)': 'scheduleHide()',
    '(focusin)': 'scheduleShow()',
    '(focusout)': 'onFocusOut($event)',
    '(keydown.escape)': 'hide()',
  },
})
export class AppTooltipDirective implements OnDestroy {
  readonly appTooltip = input<string | null>(null);
  readonly appTooltipTestId = input<string | null>(null);
  readonly appTooltipDelay = input(300);
  readonly appTooltipLinks = input<readonly { label: string; href: string }[]>([]);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly document = inject(DOCUMENT);
  private readonly tooltipId = `app-tooltip-${++nextTooltipId}`;
  private showTimer: ReturnType<typeof setTimeout> | null = null;
  private hideTimer: ReturnType<typeof setTimeout> | null = null;
  private overlay: HTMLElement | null = null;

  scheduleShow(): void {
    this.cancelShow();
    this.cancelHide();
    if (!this.appTooltip()?.trim()) return;
    const delay = Math.max(0, this.appTooltipDelay());
    if (delay === 0) {
      this.show();
      return;
    }
    this.showTimer = setTimeout(() => this.show(), delay);
  }

  onFocusOut(event: FocusEvent): void {
    if (event.relatedTarget instanceof Node && this.host.contains(event.relatedTarget)) return;
    this.hide();
  }

  hide(): void {
    this.cancelShow();
    this.cancelHide();
    if (!this.overlay) return;
    this.document.defaultView?.removeEventListener('resize', this.position);
    this.document.removeEventListener('scroll', this.position, true);
    this.overlay.remove();
    this.overlay = null;
    this.removeDescription();
  }

  ngOnDestroy(): void {
    this.hide();
  }

  private show(): void {
    this.showTimer = null;
    const content = this.appTooltip()?.trim();
    if (!content || this.overlay) return;

    const overlay = this.document.createElement('div');
    overlay.id = this.tooltipId;
    overlay.className = 'app-tooltip-overlay';
    overlay.setAttribute('role', 'tooltip');
    const testId = this.appTooltipTestId()?.trim();
    if (testId) overlay.dataset['testid'] = testId;
    const text = this.document.createElement('span');
    text.textContent = content;
    overlay.append(text);
    for (const link of this.appTooltipLinks()) {
      const anchor = this.document.createElement('a');
      anchor.textContent = link.label;
      anchor.href = link.href;
      overlay.append(anchor);
    }
    if (this.appTooltipLinks().length > 0) {
      overlay.classList.add('app-tooltip-overlay--interactive');
      overlay.addEventListener('mouseenter', () => this.cancelHide());
      overlay.addEventListener('mouseleave', () => this.hide());
    }
    this.document.body.append(overlay);
    this.overlay = overlay;
    this.addDescription();
    this.position();
    this.document.defaultView?.addEventListener('resize', this.position);
    this.document.addEventListener('scroll', this.position, true);
  }

  private readonly position = (): void => {
    const overlay = this.overlay;
    if (!overlay) return;
    const hostRect = this.host.getBoundingClientRect();
    const overlayRect = overlay.getBoundingClientRect();
    const viewportWidth = this.document.documentElement.clientWidth;
    const viewportHeight = this.document.documentElement.clientHeight;
    const edge = 8;
    const gap = 8;
    const preferredTop = hostRect.top - overlayRect.height - gap;
    const placeBelow = preferredTop < edge && hostRect.bottom + gap + overlayRect.height <= viewportHeight - edge;
    const preferredPosition = placeBelow ? hostRect.bottom + gap : preferredTop;
    const top = Math.min(
      Math.max(edge, preferredPosition),
      Math.max(edge, viewportHeight - overlayRect.height - edge),
    );
    const centeredLeft = hostRect.left + (hostRect.width - overlayRect.width) / 2;
    const left = Math.min(Math.max(edge, centeredLeft), Math.max(edge, viewportWidth - overlayRect.width - edge));

    overlay.dataset['placement'] = placeBelow ? 'bottom' : 'top';
    overlay.style.left = `${Math.round(left)}px`;
    overlay.style.top = `${Math.round(top)}px`;
  };

  private addDescription(): void {
    const ids = (this.host.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);
    if (!ids.includes(this.tooltipId)) ids.push(this.tooltipId);
    this.host.setAttribute('aria-describedby', ids.join(' '));
  }

  private removeDescription(): void {
    const ids = (this.host.getAttribute('aria-describedby') ?? '')
      .split(/\s+/)
      .filter(id => id && id !== this.tooltipId);
    if (ids.length) this.host.setAttribute('aria-describedby', ids.join(' '));
    else this.host.removeAttribute('aria-describedby');
  }

  private cancelShow(): void {
    if (this.showTimer === null) return;
    clearTimeout(this.showTimer);
    this.showTimer = null;
  }

  scheduleHide(): void {
    if (this.appTooltipLinks().length === 0) {
      this.hide();
      return;
    }
    this.cancelHide();
    this.hideTimer = setTimeout(() => this.hide(), 120);
  }

  private cancelHide(): void {
    if (this.hideTimer === null) return;
    clearTimeout(this.hideTimer);
    this.hideTimer = null;
  }
}
