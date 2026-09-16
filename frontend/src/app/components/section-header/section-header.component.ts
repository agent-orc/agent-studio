import { ChangeDetectionStrategy, Component, ViewEncapsulation, input, output } from '@angular/core';
import { StudioIconComponent, StudioIconName } from '../studio-icon/studio-icon.component';
import { DisclosureMarkerComponent } from '../disclosure-marker/disclosure-marker.component';
import { CountBadgeComponent } from '../count-badge/count-badge.component';

/**
 * The single canonical sidebar section header. Every panel group head in
 * the studio shell (Explorer / Workspaces, Agents / CLI, Runbook,
 * Orchestrator, Settings groups, Open tabs) renders through this so the
 * uppercase label, padding, divider rhythm and count pill are identical
 * across panels. It absorbed the two formerly-divergent surfaces: the
 * `app-pane-header[collapsible]` section mode and the bare
 * `studio-explorer__group-head` <div>.
 *
 * Two shapes:
 *   - static (default): a non-interactive heading <div>.
 *   - collapsible (`[collapsible]="true"`): the whole row is a toggle
 *     button — the shared `<app-disclosure-marker>` (guideline rule
 *     ADM-17) flips between expanded / collapsed and
 *     `collapsedChange` fires the flipped state so the parent can persist
 *     it (see ExplorerSectionsService). `aria-expanded` + the caller's
 *     `testid` are the contract the F27/F46 explorer-collapse specs
 *     assert on.
 *
 * `[divider]="true"` adds the top rule + spacing used to separate a
 * group from the one above it (formerly `studio-explorer__group-head--mt`).
 * The optional `[actions]` slot hosts inline affordances (e.g. the
 * Workspaces "show all" button).
 */
@Component({
  selector: 'app-section-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent, CountBadgeComponent, DisclosureMarkerComponent],
  encapsulation: ViewEncapsulation.None,
  templateUrl: './section-header.component.html',
  styleUrl: './section-header.component.scss',
})
export class SectionHeaderComponent {
  readonly title = input('');
  /** Optional count pill shown after the title. */
  readonly count = input<string | number | null>(null);
  /** Optional leading SVG glyph. */
  readonly icon = input<StudioIconName | null>(null);
  /** data-testid passthrough. */
  readonly testid = input<string | null>(null);
  /** Top rule + spacing that separates this group from the one above. */
  readonly divider = input(false);

  /** Renders the row as a collapse toggle button instead of a heading. */
  readonly collapsible = input(false);
  readonly collapsed = input(false);
  readonly collapsedChange = output<boolean>();

  onCollapseToggle(ev: Event): void {
    ev.stopPropagation();
    this.collapsedChange.emit(!this.collapsed());
  }
}
