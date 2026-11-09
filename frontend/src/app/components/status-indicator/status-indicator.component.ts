import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { StudioIconComponent, type StudioIconName } from '../studio-icon/studio-icon.component';
import { AppTooltipDirective } from '../tooltip/app-tooltip.directive';

/** The six tones every status display maps onto (AGT-3402). */
export type StatusTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral' | 'running';

/** One icon per tone, so the meaning never rests on colour alone. */
export const STATUS_TONE_ICONS: Readonly<Record<StatusTone, StudioIconName>> = {
  success: 'check',
  warning: 'warn',
  danger: 'close',
  info: 'info',
  neutral: 'circle',
  running: 'spinner',
};

/**
 * The shared status label: a tone icon in front of a visible text label,
 * tinted from the `--studio-status-*` tokens. The icon is decorative
 * (`aria-hidden`); the label is the accessible text. An optional `detail`
 * becomes a tooltip and makes the indicator focusable so keyboard users can
 * read it too. `compact` drops the tinted chip for dense rows and tables.
 *
 *   <app-status-indicator tone="success" label="Connected" />
 *
 * Contract and state-to-tone mapping: docs/quality/design/status-indicator.md.
 */
@Component({
  selector: 'app-status-indicator',
  standalone: true,
  imports: [StudioIconComponent, AppTooltipDirective],
  templateUrl: './status-indicator.component.html',
  styleUrl: './status-indicator.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StatusIndicatorComponent {
  readonly tone = input.required<StatusTone>();
  readonly label = input.required<string>();
  /** Longer explanation shown as a tooltip; `null` or empty shows none. */
  readonly detail = input<string | null | undefined>(null);
  readonly compact = input(false);
  readonly wrap = input(false);
  readonly testId = input('status-indicator');

  readonly icon = computed(() => STATUS_TONE_ICONS[this.tone()]);
  readonly tooltip = computed(() => this.detail()?.trim() || null);
}
