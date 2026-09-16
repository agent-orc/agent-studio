import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StudioIconComponent } from '../studio-icon/studio-icon.component';

/**
 * The one disclosure marker of the admin grammar (AGT-2813, guideline rule
 * ADM-17).
 *
 * Every element that expands content in place announces itself with this
 * marker: a leading chevron that points right while collapsed and rotates a
 * quarter turn while open. It is decoration only — the state a screen reader
 * hears is `aria-expanded` on the control that owns it, so the host element is
 * `aria-hidden`.
 *
 * Usage: place it as the FIRST child of the clickable element, never as a
 * trailing caret and never as a second control beside the label:
 *
 * ```html
 * <button type="button" class="thing__head"
 *         [attr.aria-expanded]="open()" (click)="toggle()">
 *   <app-disclosure-marker [open]="open()" />
 *   <span>Why this status?</span>
 * </button>
 * ```
 *
 * The hit area, hover wash and focus ring that complete the rule live in the
 * `disclosure-control` mixin (`styles/_mixins.scss`).
 */
@Component({
  selector: 'app-disclosure-marker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent],
  templateUrl: './disclosure-marker.component.html',
  styleUrl: './disclosure-marker.component.scss',
  host: {
    class: 'studio-disclosure__marker',
    'aria-hidden': 'true',
    '[class.studio-disclosure__marker--open]': 'open()',
    '[attr.data-open]': 'open() ? "true" : "false"',
  },
})
export class DisclosureMarkerComponent {
  /** Mirrors the owning control's `aria-expanded`. */
  readonly open = input.required<boolean>();
  /** Glyph size in px. 12 is the default row scale; 10 suits dense rails. */
  readonly size = input(12);
}
