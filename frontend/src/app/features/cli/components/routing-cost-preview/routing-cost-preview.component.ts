import { ChangeDetectionStrategy, Component, type OnChanges, inject, input, output, signal } from '@angular/core';
import { RoutingCostService, type PricedRouteModel, type RoutingCostEstimate } from '../../services/routing-cost.service';

@Component({
  selector: 'app-routing-cost-preview', standalone: true, changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './routing-cost-preview.component.html', styleUrl: './routing-cost-preview.component.scss',
})
export class RoutingCostPreviewComponent implements OnChanges {
  private readonly api = inject(RoutingCostService);
  readonly models = input< PricedRouteModel[]>([]);
  readonly economyMode = input<boolean | undefined>(undefined);
  readonly estimated = output<RoutingCostEstimate>();
  readonly changed = output<void>();
  readonly from = signal(new Date(Date.now() - 7 * 86400000).toISOString().slice(0, 10));
  readonly to = signal(new Date().toISOString().slice(0, 10));
  readonly consumer = signal('coding');
  readonly model = signal('');
  readonly result = signal<RoutingCostEstimate | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  private generation = 0;

  ngOnChanges(): void { this.reset(); }

  money(value: number | null): string { return value === null ? 'Unknown' : `$${value.toFixed(2)}`; }
  label(option: PricedRouteModel): string {
    return `${option.cli} · ${option.label} · ${option.price ? `$${option.price.inputPerMillion} in / $${option.price.outputPerMillion} out per MTok` : 'price unknown'}`;
  }
  reset(): void { this.generation++; this.result.set(null); this.loading.set(false); this.error.set(''); this.changed.emit(); }
  preview(): void {
    this.reset();
    if (!this.from() || !this.to() || this.from() >= this.to()) { this.error.set('Choose a start date before the end date.'); return; }
    const generation = this.generation;
    this.loading.set(true);
    this.api.estimate(`${this.from()}T00:00:00Z`, `${this.to()}T00:00:00Z`, this.consumer(),
      this.model() || '__economy__', this.economyMode()).subscribe({
      next: result => { if (generation !== this.generation) return; this.result.set(result); this.loading.set(false); this.estimated.emit(result); },
      error: () => { if (generation !== this.generation) return; this.error.set('Cost preview could not be loaded. Select at most 93 days.'); this.loading.set(false); },
    });
  }
}
