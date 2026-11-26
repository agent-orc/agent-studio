import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { LiveGlyphComponent } from '../live-glyph/live-glyph.component';
import { LIVE_LAYERS, type LiveLayer } from '../live-structure.model';

/**
 * The layer switch of the live structure view: five labelled switches, each with
 * the sign its layer draws on the canvas, one fact from the layer and a written
 * On or Off. Layers combine, so these are switches and not a single choice.
 */
@Component({
  selector: 'app-live-structure-layers',
  standalone: true,
  imports: [AppTooltipDirective, LiveGlyphComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './live-structure-layers.component.html',
  styleUrl: './live-structure-layers.component.scss',
})
export class LiveStructureLayersComponent {
  readonly layers = input.required<Readonly<Record<LiveLayer, boolean>>>();
  /** One short fact per layer, shown whether the layer is on or off. */
  readonly summaries = input.required<Readonly<Record<LiveLayer, string>>>();
  readonly layerToggle = output<LiveLayer>();
  readonly all = LIVE_LAYERS;
}
