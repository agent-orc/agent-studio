import { ChangeDetectionStrategy, Component, ViewEncapsulation, input } from '@angular/core';

/**
 * The single canonical count pill used across the studio sidebar — panel
 * section headers (Workspaces, Open tabs), tree rows (project totals,
 * workspace project counts) and the CLI agent rows. Before this existed
 * the same badge was hand-rolled four times with drifting padding /
 * radius / colour (`section-header__count`, `studio-explorer__count`,
 * `tree-row__count`, `studio-cli__count`); ASS-707 (count-badge padding)
 * now lives in one place so every count reads identically.
 *
 *   <app-count-badge [value]="42" />
 *
 * Pass `tone="active"` when the row owning the badge is selected so the
 * pill inverts to the accent fill instead of the muted elevated chip.
 * Pass `variant="pane-tab"` for tab-strip metadata counts, where the badge
 * needs to stay calmer and align with the tab icon/label row.
 */
@Component({
  selector: 'sui-count-badge, app-count-badge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './count-badge.component.html',
  styleUrl: './count-badge.component.scss',
})
export class CountBadgeComponent {
  /** The number/label to show. `null` renders nothing. */
  readonly value = input<string | number | null>(null);
  /** `active` inverts the pill to the accent fill for selected rows. */
  readonly tone = input<'default' | 'active'>('default');
  /** Context variant. Defaults preserve the canonical sidebar/tree pill. */
  readonly variant = input<'default' | 'pane-tab'>('default');
}
