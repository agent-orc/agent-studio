import { ChangeDetectionStrategy, Component, ViewEncapsulation, input, output } from '@angular/core';
import { StudioIconComponent, StudioIconName } from '../studio-icon/studio-icon.component';

/**
 * The detail-view pane header: icon + title + actions slot + maximize /
 * hide buttons used by the prompt / protocol / git panes. The prompt
 * pane's three-tab strip renders inside the projected `tabs` slot instead
 * of the title; pass an empty title in that case and project the tabs.
 *
 * The sidebar/explorer collapsible group headers (Workspaces, Open tabs,
 * Agents / CLI, …) live in the separate `app-section-header`; this
 * component is pane chrome only.
 *
 * See docs/quality/frontend/audits/scss-quality.md "Wave C".
 */
@Component({
  selector: 'sui-pane-header, app-pane-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent],
  encapsulation: ViewEncapsulation.None,
  templateUrl: './pane-header.component.html',
  styleUrl: './pane-header.component.scss',
})
export class PaneHeaderComponent {
  readonly icon = input<StudioIconName | null>(null);
  /**
   * Optional emoji/glyph icon. Used by panes whose title still carries a
   * Unicode glyph (e.g. the protocol pane's "📜 Protocol"). Renders next
   * to or instead of {@link icon} so callers can opt into the legacy
   * emoji vocabulary without losing the shared chrome.
   */
  readonly iconEmoji = input<string | null>(null);
  readonly title = input('');
  readonly maximized = input(false);
  readonly maximizable = input(true);
  readonly hideable = input(true);
  /** Optional extra subtitle that follows the title. */
  readonly subtitle = input<string | null>(null);
  /** data-testid passthrough. */
  readonly testid = input<string | null>(null);
  /** Optional modifier class applied to the host header (e.g. brand wash). */
  readonly modifier = input<string | null>(null);

  readonly maximize = output<void>();
  readonly hide = output<void>();
}
