import { ChangeDetectionStrategy, Component, ViewEncapsulation, input } from '@angular/core';

/**
 * Shared label/value row primitive. Variants resolve to the density tokens
 * declared in `_tokens-semantic.scss` (`--studio-row-pad-block-*`,
 * `--studio-row-min-h-*`, `--studio-row-gap-*`). Use this anywhere the UI
 * shows a flat "label on the left, value on the right" pattern (status
 * facts, settings entries, activity facts) and you want it to track the
 * project-wide density rules instead of carrying its own px.
 *
 * Slots project via attribute selectors so the caller does not need an
 * extra wrapper element:
 *   <app-row variant="compact">
 *     <span appRowLabel>Lane</span>
 *     <span appRowValue>Ready</span>
 *   </app-row>
 *
 * `variant`:
 *   - omitted           - inherit the root or containing surface density
 *   - compact           - explicit compact override
 *   - default           — mid-density panels
 *   - cozy              — empty states, Welcome cards
 *
 * `interactive`:
 *   - false (default)   — static row, smaller min-height floor
 *   - true              — clickable / keyboard-focusable, lifts the floor
 *                         to the WCAG-AA touch target (32/36/40px)
 */
@Component({
  selector: 'sui-row, app-row',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './row.component.html',
  styleUrl: './row.component.scss',
  host: {
    '[attr.data-variant]': 'variant()',
    '[attr.data-interactive]': 'interactive() ? "" : null',
  },
})
export class RowComponent {
  readonly variant = input<'compact' | 'default' | 'cozy'>();
  readonly interactive = input(false);
}
