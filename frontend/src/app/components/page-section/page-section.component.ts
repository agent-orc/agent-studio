import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { CountBadgeComponent } from '@studio/ui/components/count-badge/count-badge.component';

/**
 * The frame of one section on a settings or admin page: a heading, one
 * sentence of purpose, the count of what it lists, and at most one primary
 * action. The body stays flat; a section never draws a card around itself.
 *
 *   <app-page-section anchor="access-people" title="People" [count]="6"
 *                     purpose="People with their own sign-in.">
 *     <button sectionAction type="button" class="btn btn--primary">Invite person</button>
 *     <app-data-grid ... />
 *   </app-page-section>
 *
 * `anchor` is the element id a `<app-page-outline>` jumps to. `level="sub"`
 * renders a part of a section (one level down, no divider).
 */
@Component({
  selector: 'app-page-section',
  standalone: true,
  imports: [CountBadgeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './page-section.component.html',
  styleUrl: './page-section.component.scss',
})
export class PageSectionComponent {
  readonly anchor = input.required<string>();
  readonly title = input.required<string>();
  readonly purpose = input<string | null>(null);
  /** Number of entries the section lists; `null` shows no count. It must equal the rows shown. */
  readonly count = input<number | null>(null);
  readonly level = input<'section' | 'sub'>('section');
  /** The hairline above a section; the first section under a page head or an outline needs none. */
  readonly divider = input(true);
  readonly testId = input<string | null>(null);

  readonly titleId = computed(() => `${this.anchor()}-title`);
}
