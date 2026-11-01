import {
  ChangeDetectionStrategy,
  Component,
  ViewEncapsulation,
  input,
  output,
} from '@angular/core';
import { StudioIconComponent, StudioIconName } from '../studio-icon/studio-icon.component';

/**
 * One choice in a {@link SegmentedControlComponent}.
 *
 * The control is data-driven (vs. content projection per option) so every
 * caller hands it an array and the visual chrome: raised thumb, dimmed
 * inactive, hover, focus ring — stays identical across surfaces.
 */
export interface SegmentedOption<T extends string | number = string> {
  /** Stable value emitted via `valueChange` and compared against `value`. */
  readonly value: T;
  /** User-facing label rendered in the segment. */
  readonly label: string;
  /** Optional studio icon rendered before the label; omitted → text only. */
  readonly icon?: StudioIconName;
  /** Stable test hook; falls back to no `data-testid`. */
  readonly testid?: string;
  /** Disables this one segment but keeps it in the strip. */
  readonly disabled?: boolean;
  /** Optional native tooltip for a choice. */
  readonly tooltip?: string;
}

/**
 * Shared two-or-more option segmented control (single-select toggle).
 *
 * Used for the Settings Appearance/Layout switches (Theme Dark|Light,
 * Activity bar Left|Right) and any future "pick one of N" control that
 * wants the same look. Renders a `role="group"` container with one
 * `<button>` per option; the selected option carries `aria-pressed="true"`
 * and a raised thumb, the rest stay dimmed, so the active choice reads
 * at a glance in both light and dark themes.
 */
@Component({
  selector: 'app-segmented-control',
  standalone: true,
  imports: [StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './segmented-control.component.html',
  styleUrl: './segmented-control.component.scss',
})
export class SegmentedControlComponent<T extends string | number = string> {
  readonly options = input.required<readonly SegmentedOption<T>[]>();
  readonly value = input.required<T>();
  /** Accessible name for the whole group (e.g. "Theme", "Activity bar"). */
  readonly ariaLabel = input<string | null>(null);
  /** Disables the full choice group during an operation. */
  readonly disabled = input(false);

  readonly valueChange = output<T>();

  trackOption(_index: number, option: SegmentedOption<T>): string | number {
    return option.value;
  }

  select(option: SegmentedOption<T>): void {
    if (this.disabled() || option.disabled) return;
    if (option.value === this.value()) return;
    this.valueChange.emit(option.value);
  }
}
