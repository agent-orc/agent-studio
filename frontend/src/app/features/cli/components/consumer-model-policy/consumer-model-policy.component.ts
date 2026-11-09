import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { QuotaApiService } from '../../../quota';
import type { ConsumerModelPolicy, ConsumerPolicyRoute, EffectiveModelSelection, ModelSelectionPolicy } from '../../../quota/services/quota-api.service';
import { scrollCliModelsTable } from '../cli-models-panel/cli-models-table-keyboard';

@Component({
  selector: 'app-consumer-model-policy',
  standalone: true,
  templateUrl: './consumer-model-policy.component.html',
  styleUrl: './consumer-model-policy.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConsumerModelPolicyComponent implements OnInit {
  readonly onTableScrollKeydown = scrollCliModelsTable;
  private readonly api = inject(QuotaApiService);
  readonly policy = signal<ModelSelectionPolicy | null>(null);
  readonly effectiveRoutes = signal<EffectiveModelSelection[]>([]);
  readonly saving = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly rows = computed(() => Object.entries(this.policy()?.consumers ?? {})
    .map(([id, setting]) => ({ id, setting })));

  ngOnInit(): void {
    this.api.getSelectionPolicy().subscribe({
      next: (policy) => this.policy.set(policy),
      error: () => this.error.set('Model selection policy could not be loaded.'),
    });
    this.refreshEffective();
  }

  routeText(route: ConsumerPolicyRoute | null): string {
    return route ? [route.cli, route.model ?? '', route.thinking ?? ''].join(':') : '';
  }

  fallbackText(setting: ConsumerModelPolicy): string {
    return setting.fallbacks === 'catalogue' ? 'catalogue'
      : (setting.fallbacks ?? []).map((route) => this.routeText(route)).join(', ');
  }

  private parseRoute(value: string): ConsumerPolicyRoute | null {
    const [cli, model, thinking] = value.trim().split(':').map((part) => part.trim());
    return cli ? { cli, model: model || null, thinking: thinking || null } : null;
  }

  update(id: string, field: 'primary' | 'fallbacks', value: string): void {
    const current = this.policy();
    const setting = current?.consumers[id];
    if (!current || !setting || this.saving()) return;
    const parsed = field === 'primary'
      ? { primary: this.parseRoute(value) }
      : { fallbacks: value.trim() === 'catalogue' ? 'catalogue' as const
        : value.split(',').map((part) => this.parseRoute(part)).filter((route): route is ConsumerPolicyRoute => route !== null) };
    this.saving.set(id);
    this.error.set(null);
    this.api.setConsumerPolicy(id, { ...setting, ...parsed }).subscribe({
      next: (policy) => { this.policy.set(policy); this.saving.set(null); this.refreshEffective(); },
      error: () => { this.error.set(`Could not save ${id}.`); this.saving.set(null); },
    });
  }

  private refreshEffective(): void {
    this.api.getEffectiveModelSelection().subscribe({
      next: (response) => this.effectiveRoutes.set(response.routes),
    });
  }
}
