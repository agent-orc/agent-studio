import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import type { ModelRoutingPolicyView } from '../../../quota/services/quota-api.service';
import type { RoutingCostEstimate } from '../../services/routing-cost.service';
import { RoutingCostPreviewComponent } from '../routing-cost-preview/routing-cost-preview.component';

@Component({
  selector: 'app-economy-routing-preview', standalone: true, changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RoutingCostPreviewComponent], templateUrl: './economy-routing-preview.component.html',
  styleUrl: './economy-routing-preview.component.scss',
})
export class EconomyRoutingPreviewComponent {
  readonly policy = input.required<ModelRoutingPolicyView>();
  readonly saving = input(false);
  readonly saveRequested = output<boolean>();
  readonly draft = signal<boolean | null>(null);
  readonly preview = signal<RoutingCostEstimate | null>(null);

  stage(enabled: boolean): void { this.draft.set(enabled); this.preview.set(null); }
  save(): void {
    const draft = this.draft();
    if (draft === null || !this.preview() || this.saving()) return;
    this.saveRequested.emit(draft);
    this.draft.set(null);
    this.preview.set(null);
  }
}
