import { Directive, ElementRef, OnDestroy, effect, inject, input } from '@angular/core';

/**
 * AGT-3580 - a remedy link opens the project setting that owns the fix. The
 * Settings rail renders its cards lazily, so this watches the panel until the
 * field marked `data-setting-key="<key>"` exists, then scrolls it into view,
 * marks it `aria-current="location"`, and moves focus there.
 * Same shape as `PipelineStepFocusDirective` for pipeline rows.
 */
@Directive({
  selector: '[appSettingFocus]',
  standalone: true,
})
export class SettingFocusDirective implements OnDestroy {
  readonly focusSettingKey = input<string | undefined>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly observer = new MutationObserver(() => this.focusMatchingSetting());
  private focused: HTMLElement | null = null;
  private focusedKey: string | undefined;

  constructor() {
    this.observer.observe(this.host.nativeElement, { childList: true, subtree: true });
    effect(() => {
      this.focusSettingKey();
      queueMicrotask(() => this.focusMatchingSetting());
    });
  }

  ngOnDestroy(): void {
    this.observer.disconnect();
  }

  private focusMatchingSetting(): void {
    const key = this.focusSettingKey();
    if (!key) {
      this.clear();
      return;
    }
    if (key === this.focusedKey && this.focused?.isConnected) return;
    const target = Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>('[data-setting-key]'))
      .find(element => element.dataset['settingKey'] === key);
    if (!target) return;

    this.clear();
    this.focused = target;
    this.focusedKey = key;
    target.setAttribute('aria-current', 'location');
    target.scrollIntoView?.({ block: 'center' });
    // A text field or select takes focus; a choice of buttons does not, since
    // focusing one option reads as a preselection. The field itself takes it.
    const control = target.querySelector<HTMLElement>('input, select, textarea');
    if (!control && !target.hasAttribute('tabindex')) target.tabIndex = -1;
    (control ?? target).focus({ preventScroll: true });
  }

  private clear(): void {
    this.focused?.removeAttribute('aria-current');
    this.focused = null;
    this.focusedKey = undefined;
  }
}
