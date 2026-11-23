import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { QuotaApiService } from '../../../quota';
import type { ConsumerModelPolicy, ConsumerPolicyRoute, EffectiveModelSelection, ModelSelectionPolicy } from '../../../quota/services/quota-api.service';
import { RoutingCostService, type PricedRouteModel, type RoutingCostEstimate } from '../../services/routing-cost.service';
import { RoutingCostPreviewComponent } from '../routing-cost-preview/routing-cost-preview.component';
import { scrollCliModelsTable } from '../cli-models-panel/cli-models-table-keyboard';

@Component({
  selector: 'app-consumer-model-policy',
  standalone: true,
  imports: [FormsModule, RoutingCostPreviewComponent],
  templateUrl: './consumer-model-policy.component.html',
  styleUrl: './consumer-model-policy.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConsumerModelPolicyComponent implements OnInit {
  readonly onTableScrollKeydown = scrollCliModelsTable;
  private readonly api = inject(QuotaApiService);
  private readonly costs = inject(RoutingCostService);
  readonly models = signal<PricedRouteModel[]>([]);
  readonly drafts = signal<Record<string, ConsumerModelPolicy>>({});
  readonly previews = signal<Record<string, RoutingCostEstimate | null>>({});
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
    this.costs.models().subscribe({ next: response => this.models.set(response.models), error: () => this.error.set('Model prices could not be loaded. Existing routes remain visible.') });
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

  setting(id: string, original: ConsumerModelPolicy): ConsumerModelPolicy { return this.drafts()[id] ?? original; }

  options(route: ConsumerPolicyRoute | null): PricedRouteModel[] {
    const models = this.models();
    if (!route || models.some(model => model.cli === route.cli && model.model === (route.model ?? ''))) return models;
    return [{ cli: route.cli, model: route.model ?? '', label: route.model ?? 'CLI default', thinkingLevels: route.thinking ? [route.thinking] : [], price: null }, ...models];
  }

  routeKey(route: ConsumerPolicyRoute | null): string { return route ? `${route.cli}:${route.model ?? ''}` : ''; }
  optionValue(model: PricedRouteModel): string { return `${model.cli}:${model.model}`; }

  modelLabel(model: PricedRouteModel): string {
    return `${model.cli} · ${model.label} · ${model.price ? `$${model.price.inputPerMillion} in / $${model.price.outputPerMillion} out per MTok` : 'price unknown'}`;
  }

  routeSummary(route: ConsumerPolicyRoute | null): string {
    if (!route) return 'Inherit default';
    const model = this.options(route).find(option => option.cli === route.cli && option.model === route.model);
    return model ? this.modelLabel(model) : this.routeText(route);
  }

  fallbackRoutes(setting: ConsumerModelPolicy): ConsumerPolicyRoute[] { return setting.fallbacks === 'catalogue' ? [] : setting.fallbacks ?? []; }

  stage(id: string, patch: Partial<ConsumerModelPolicy>): void {
    const original = this.policy()?.consumers[id];
    if (!original || this.saving()) return;
    this.drafts.update(drafts => ({ ...drafts, [id]: { ...this.setting(id, original), ...patch } }));
    this.previews.update(previews => ({ ...previews, [id]: null }));
  }

  update(id: string, field: 'primary' | 'fallbacks', value: string): void {
    if (field === 'primary') this.stage(id, { primary: this.parseRoute(value) });
    else this.stage(id, { fallbacks: value === 'catalogue' ? 'catalogue' : value.split(',').map(part => this.parseRoute(part)).filter((route): route is ConsumerPolicyRoute => route !== null) });
  }

  selectModel(id: string, value: string, index?: number): void {
    const original = this.policy()?.consumers[id];
    if (!original) return;
    const setting = this.setting(id, original);
    const route = this.parseRoute(value);
    if (index === undefined) this.stage(id, { primary: route });
    else if (route) { const fallbacks = [...this.fallbackRoutes(setting)]; fallbacks[index] = route; this.stage(id, { fallbacks }); }
  }

  fallbackThinking(id: string, index: number, thinking: string): void {
    const original = this.policy()?.consumers[id];
    if (!original) return;
    const fallbacks = [...this.fallbackRoutes(this.setting(id, original))];
    fallbacks[index] = { ...fallbacks[index], thinking: thinking || null };
    this.stage(id, { fallbacks });
  }

  addFallback(id: string): void {
    const original = this.policy()?.consumers[id];
    const first = this.models()[0];
    if (original && first) this.stage(id, { fallbacks: [...this.fallbackRoutes(this.setting(id, original)), { cli: first.cli, model: first.model, thinking: null }] });
  }
  removeFallback(id: string, index: number): void {
    const original = this.policy()?.consumers[id];
    if (original) this.stage(id, { fallbacks: this.fallbackRoutes(this.setting(id, original)).filter((_, i) => i !== index) });
  }

  preview(id: string): void {
    const draft = this.drafts()[id];
    const model = draft?.primary?.model;
    if (!model) return;
    this.error.set(null);
    const to = new Date().toISOString();
    this.costs.estimate(new Date(Date.now() - 7 * 86400000).toISOString(), to,
      id === 'coding' || id === 'review-aspect' ? id : 'one-shot', model).subscribe({
      next: result => { if (this.drafts()[id] === draft) this.previews.update(previews => ({ ...previews, [id]: result })); },
      error: () => this.error.set('Weekly cost preview could not be loaded.'),
    });
  }

  save(id: string): void {
    const draft = this.drafts()[id];
    if (!draft || this.saving() || (draft.primary?.model && !this.previews()[id])) return;
    this.saving.set(id);
    this.error.set(null);
    this.api.setConsumerPolicy(id, draft).subscribe({
      next: policy => { this.policy.set(policy); this.saving.set(null); this.drafts.update(drafts => { const next = { ...drafts }; delete next[id]; return next; }); this.refreshEffective(); },
      error: () => { this.error.set(`Could not save ${id}.`); this.saving.set(null); },
    });
  }

  effectiveFor(id: string): EffectiveModelSelection[] {
    return [...new Map(this.effectiveRoutes().filter(route => route.consumer === id)
      .map(route => [route.stepId ?? 'consumer-default', route])).values()];
  }

  private refreshEffective(): void {
    this.api.getEffectiveModelSelection().subscribe({
      next: (response) => this.effectiveRoutes.set(response.routes),
    });
  }
}
