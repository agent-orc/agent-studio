import { ChangeDetectionStrategy, Component, ViewEncapsulation, input } from '@angular/core';
import { LegacyTooltipDirective as TooltipDirective } from '../tooltip/legacy-tooltip.directive';

/**
 * Shared Studio sidebar header chrome. Explorer, Filters, and Project Hub use
 * the same compact title/action row instead of each feature carrying its own
 * header spacing.
 */
@Component({
  selector: 'sui-studio-sidebar-header, app-studio-sidebar-header',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './studio-sidebar-header.component.html',
  styleUrl: './studio-sidebar-header.component.scss',
})
export class StudioSidebarHeaderComponent {
  readonly title = input.required<string>();
  readonly subtitle = input<string | null>(null);
  readonly subtitleTestid = input<string | null>(null);
  readonly testid = input<string | null>(null);
}
