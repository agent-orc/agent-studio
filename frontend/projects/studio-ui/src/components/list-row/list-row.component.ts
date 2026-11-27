import { ChangeDetectionStrategy, Component, ViewEncapsulation, input, output } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';

/**
 * The single canonical flat list row for the studio sidebar panels. A row is
 * one line: an optional leading glyph/icon (`[lead]` slot), a truncating
 * label, and an optional trailing affordance (`[trail]` slot — e.g. an
 * `app-count-badge` or a close button). It replaces the per-panel row markup
 * that had drifted apart (`studio-cli__row` for the Agents/CLI list,
 * `studio-tree-row--open-tab` for the Open-tabs list) so every flat row shares
 * the same height, gap, padding, hover wash and selected state.
 *
 * Two shapes:
 *   - static (default): a non-interactive `<div>` (e.g. the CLI agent rows).
 *   - interactive (`[interactive]="true"`): the whole row is a button that
 *     emits `activated` on click and shows the `--active` selection wash when
 *     `[active]` is set (e.g. the Open-tabs rows).
 *
 * `[capitalize]` capitalises the label (the CLI list shows raw `cliType`
 * strings title-cased). `[indent]` overrides the left padding for rows that
 * must align to a surrounding grid (the Open-tabs rows align to the Explorer
 * tree's project-glyph column, not the default 12px gutter).
 */
@Component({
  selector: 'sui-list-row, app-list-row',
  standalone: true,
  imports: [NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './list-row.component.html',
  styleUrl: './list-row.component.scss',
})
export class ListRowComponent {
  readonly label = input('');
  /** Render as a clickable button (emits `activated`) instead of a `<div>`. */
  readonly interactive = input(false);
  /** Selected-row wash; only meaningful with `[interactive]`. */
  readonly active = input(false);
  /** Title-case the label (used by the Agents/CLI list). */
  readonly capitalize = input(false);
  /** Left-padding override (CSS length) for rows that align to an outer grid. */
  readonly indent = input<string | null>(null);
  /** data-testid passthrough. */
  readonly testid = input<string | null>(null);

  readonly activated = output<void>();
}
