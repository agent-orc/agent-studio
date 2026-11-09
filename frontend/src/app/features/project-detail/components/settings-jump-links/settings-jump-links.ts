import { ChangeDetectionStrategy, Component, DOCUMENT, inject, input } from '@angular/core';

export interface SettingsJumpLink {
  /** Element id of the target section on the same page. */
  target: string;
  label: string;
  testId: string;
}

/**
 * In-page links at the top of a settings page. A plain `#id` href would
 * replace the hash route, so each jump scrolls and focuses in code (AGT-3592).
 */
@Component({
  selector: 'app-settings-jump-links',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './settings-jump-links.html',
  styleUrl: './settings-jump-links.scss',
})
export class SettingsJumpLinksComponent {
  readonly links = input.required<readonly SettingsJumpLink[]>();
  private readonly document = inject(DOCUMENT);

  jump(id: string): void {
    const target = this.document.getElementById(id);
    if (!target) return;
    const reduceMotion = this.document.defaultView?.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
    target.scrollIntoView({ behavior: reduceMotion ? 'auto' : 'smooth', block: 'start' });
    if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1');
    target.focus({ preventScroll: true });
  }
}
