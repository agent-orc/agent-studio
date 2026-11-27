import { ChangeDetectionStrategy, Component, ViewEncapsulation, input, output } from '@angular/core';
import { StudioIconComponent, StudioIconName } from '@studio/ui/components/studio-icon/studio-icon.component';
import { TooltipDirective } from 'coding-agent-chat/shared';

/**
 * Single status-bar chip: icon + label + click. Six call sites in the
 * status bar repeated the same `<button class="statusbar__item">` skeleton;
 * this component owns the layout + hover state so additions stay
 * one-line.
 *
 * The component preserves the legacy class names (`statusbar__item`,
 * `statusbar__icon`) so existing SCSS rules + Playwright selectors
 * continue to match.
 */
@Component({
  selector: 'app-statusbar-item',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent, TooltipDirective],
  encapsulation: ViewEncapsulation.None,
  templateUrl: './statusbar-item.component.html',
  styleUrls: [],
})
export class StatusbarItemComponent {
  readonly icon = input<StudioIconName | null>(null);
  readonly iconSize = input(12);
  readonly label = input('');
  /** Tooltip text — uses the project's TooltipDirective. */
  readonly tooltip = input('');
  /** Renders as a button (default true) vs read-only text chip. */
  readonly button = input(true);
  /** Pressed/toggled state — bound to the open flag of the panel this
   *  button opens, so the bar shows which overlay is currently visible.
   *  Drives the `--active` class and `aria-pressed`. Read-only chips
   *  ignore it. */
  readonly active = input(false);
  readonly testid = input<string | null>(null);
  /** Animate the icon as a live indicator (e.g. the "● running" chip). */
  readonly pulsing = input(false);
  /** Optional low-noise signal tone for a read-only status indicator. */
  readonly signalTone = input<'unknown' | 'calm' | 'working' | 'hot' | 'mismatch' | null>(null);
  /** Machine-readable correlation state for visual and E2E inspection. */
  readonly signalCorrelation = input<string | null>(null);
  /** Small, explicit warning when independent live sources disagree. */
  readonly warning = input(false);
  readonly warningLabel = input('Running sources disagree');
  /** Bullet character used for read-only chips that pre-date the SVG icon
   *  set ("● running" / "↻ N/M auto"); takes precedence over `icon` so
   *  callers can keep the legacy glyph without forcing every status-bar
   *  chip to a flat SVG. */
  readonly bullet = input<string | null>(null);

  readonly activated = output<MouseEvent>();
}
