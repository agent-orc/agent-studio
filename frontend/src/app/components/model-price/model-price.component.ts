import { ChangeDetectionStrategy, Component, computed, effect, inject, input, untracked } from '@angular/core';
import { ModelPriceStore, modelPriceLabel } from '../../features/tokens/model-prices';
import { AppTooltipDirective } from '../tooltip/app-tooltip.directive';

/**
 * AGT-2903: compact current list price for one model ("$2 / $10"), read from
 * the TokenEconomy cost API through {@link ModelPriceStore}. Renders nothing
 * while the price loads or when TokenEconomy does not know the model.
 */
@Component({
  selector: 'app-model-price',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './model-price.component.html',
  styleUrl: './model-price.component.scss',
})
export class ModelPriceComponent {
  private readonly prices = inject(ModelPriceStore);
  readonly modelId = input.required<string>();
  readonly testId = input('model-price');
  readonly label = computed(() => modelPriceLabel(this.prices.prices().get(this.modelId())));

  constructor() {
    effect(() => {
      const id = this.modelId();
      untracked(() => this.prices.ensure([id]));
    });
  }
}
