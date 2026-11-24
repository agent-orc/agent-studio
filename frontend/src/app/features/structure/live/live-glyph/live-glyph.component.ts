import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { LiveKind, LiveLayer } from '../live-structure.model';

/**
 * The signs of the live structure view: one per layer and one per kind of node.
 * The same sign marks a layer in the switch, in the card details and on the
 * canvas rows, and a kind in the outline and the legend. Purely decorative;
 * the text beside it names the thing.
 */
@Component({
  selector: 'app-live-glyph',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './live-glyph.component.html',
  styleUrl: './live-glyph.component.scss',
})
export class LiveGlyphComponent {
  readonly name = input.required<LiveLayer | LiveKind>();
  /** Layer signs are drawn quiet while their layer is off. */
  readonly off = input(false);
}
