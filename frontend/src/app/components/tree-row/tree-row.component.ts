import { ChangeDetectionStrategy, Component, ViewEncapsulation, input, output } from '@angular/core';
import { StudioIconComponent, StudioIconName } from '../studio-icon/studio-icon.component';
import { CountBadgeComponent } from '../count-badge/count-badge.component';
import { AppTooltipDirective } from '../tooltip/app-tooltip.directive';

/**
 * Reusable tree row used in the studio shell sidebar (Explorer
 * workspace tree, Tasks outline, legacy `.tree-row`). Centralises the
 * chevron + glyph + label + count layout so the three places that
 * repeat the pattern read from one source.
 *
 * Outline:
 *   [chevron] [glyph] label [meta] [count badge]
 *
 * Caller chooses which slots to fill via inputs. Two visual variants:
 *   - level="root"  — workspace / project rows (8 px left padding)
 *   - level="child" — sub rows (44 px left padding to nest under the
 *                     parent's glyph)
 *
 * The component preserves the legacy BEM class names so existing
 * styles.scss bridge entries continue to apply without rewrite.
 */
@Component({
  selector: 'app-tree-row',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent, CountBadgeComponent, AppTooltipDirective],
  encapsulation: ViewEncapsulation.None,
  templateUrl: './tree-row.component.html',
  styleUrl: './tree-row.component.scss',
})
export class TreeRowComponent {
  /** Visible chevron — pass `null` to suppress the chevron column. */
  readonly chevron = input<'collapsed' | 'expanded' | null>(null);
  /** Keep the chevron column even when the row itself has no chevron. */
  readonly reserveChevron = input(false);
  /** Optional SVG glyph; takes precedence over `glyphChar`. */
  readonly glyph = input<StudioIconName | null>(null);
  /** Optional initial letter (e.g. project avatar "A"). */
  readonly glyphChar = input<string | null>(null);
  /** Keep the glyph column even when the row has no icon or initial. */
  readonly reserveGlyph = input(false);
  /** Coloured square behind the glyph initial. */
  readonly glyphColor = input<string | null>(null);
  readonly label = input('');
  readonly count = input<string | number | null>(null);
  readonly meta = input<string | null>(null);
  /** Complete row context shown through the viewport-safe shared tooltip. */
  readonly tooltip = input<string | null>(null);
  readonly tooltipTestId = input<string | null>(null);
  readonly ariaLabel = input<string | null>(null);
  readonly ariaControls = input<string | null>(null);
  readonly active = input(false);
  readonly level = input<'root' | 'child'>('root');
  readonly testid = input<string | null>(null);
  /** `aria-current` value for the row button (e.g. 'page' for the active nav item). */
  readonly ariaCurrent = input<'page' | 'true' | null>(null);
  /** data-testid for the chevron control — lets callers assert on the disclosure twisty. */
  readonly chevronTestid = input<string | null>(null);
  readonly disabled = input(false);

  readonly chevronClick = output<Event>();
  readonly selectRequest = output<Event>();
  readonly secondary = output<Event>();

  onChevronClick(ev: Event): void {
    ev.stopPropagation();
    this.chevronClick.emit(ev);
  }

  onSelect(ev: Event): void {
    if (this.disabled()) return;
    this.selectRequest.emit(ev);
  }

  onSecondary(ev: Event): void {
    this.secondary.emit(ev);
  }
}
