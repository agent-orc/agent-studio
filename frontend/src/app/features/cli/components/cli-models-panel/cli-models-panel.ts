import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { CLI_TYPES, type CliType } from '../../../../models/task.model';
import type { CliModelInfo } from '../../models/cli.model';
import { CliCatalogStore } from '../../services/cli-catalog.store';
import { cliTypeIcon, cliTypeLabel, formatCompactDateTime } from '../../../../services/format.util';
import { clearVisibleInterval, setVisibleInterval, type VisibleIntervalHandle } from '../../../../utils/visible-interval';
import { QuotaApiService, type CliCatalogueEquivalentRoute, type CliModelRouteProfile, type CliQuotaActiveFallback,
  type ModelRoutingPolicyView } from '../../../quota';

interface CliModelGroup {
  cliType: CliType;
  label: string;
  icon: string;
  models: readonly CliModelInfo[];
  defaultModel: CliModelInfo | null;
}

/**
 * Per-CLI model catalog overview for the CLI Management page (rows since
 * AGT-2101): each known CLI is one compact, stacked row that answers "what's
 * present" at a glance - the primary model and the fallback-route state - and
 * expands to reveal the route editor (primary / fallback CLI + model + thinking)
 * and the full discovered model list. Data is the live `/api/cli/{type}/models`
 * catalog, read through the process-wide {@link CliCatalogStore} so the page
 * reuses the boot-time hydration instead of issuing its own per-CLI requests.
 * The refresh button forces a re-probe of one CLI's catalog (bypasses the store
 * TTL).
 */
@Component({
  selector: 'app-cli-models-panel',
  standalone: true,
  imports: [],
  templateUrl: './cli-models-panel.html',
  styleUrl: './cli-models-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CliModelsPanelComponent implements OnInit, OnDestroy {
  private readonly catalog = inject(CliCatalogStore);
  private readonly routesApi = inject(QuotaApiService);
  readonly routes = signal<Record<string, CliModelRouteProfile>>({});
  readonly catalogueRoutes = signal<readonly CliCatalogueEquivalentRoute[]>([]);
  readonly savingCli = signal<string | null>(null);
  readonly policy = signal<ModelRoutingPolicyView | null>(null);
  readonly savingEconomyMode = signal(false);
  readonly cliTypes = CLI_TYPES;
  private routesPoll: VisibleIntervalHandle | null = null;

  /** CLIs whose per-row details (route editor + full model list) are expanded.
   *  Collapsed rows still answer "what's present" via the summary line. */
  readonly expanded = signal<Set<string>>(new Set());

  /** One group per known CLI. Recomputes when the store's catalog map updates. */
  readonly groups = computed<CliModelGroup[]>(() =>
    CLI_TYPES.map((cliType) => {
      const models = this.catalog.modelsFor(cliType);
      return {
        cliType,
        label: cliTypeLabel(cliType),
        icon: cliTypeIcon(cliType),
        models,
        defaultModel: models.find((m) => m.isDefault) ?? null,
      };
    }),
  );

  ngOnInit(): void {
    this.catalog.hydrateAll();
    this.loadRoutes();
    this.routesPoll = setVisibleInterval(() => this.loadRoutes(), 60_000);
    this.routesApi.getModelRoutingPolicy().subscribe({
      next: (policy) => this.policy.set(policy),
    });
  }

  ngOnDestroy(): void {
    clearVisibleInterval(this.routesPoll);
  }

  refresh(cliType: CliType): void {
    this.catalog.refresh(cliType).subscribe({ error: () => void 0 });
  }

  toggle(cliType: CliType): void {
    const next = new Set(this.expanded());
    if (next.has(cliType)) next.delete(cliType);
    else next.add(cliType);
    this.expanded.set(next);
  }

  isExpanded(cliType: CliType): boolean {
    return this.expanded().has(cliType);
  }

  thinkingSummary(m: CliModelInfo): string {
    return m.thinkingLevels?.length ? m.thinkingLevels.join(' · ') : '';
  }

  primaryModel(cliType: CliType): string {
    return this.routes()[cliType]?.primaryModel
      ?? this.catalog.modelsFor(cliType).find((m) => m.isDefault)?.id
      ?? '';
  }

  /** Human label of the resolved primary model, for the collapsed summary. */
  primaryModelLabel(cliType: CliType): string {
    const id = this.primaryModel(cliType);
    if (!id) return 'no catalog';
    return this.catalog.modelsFor(cliType).find((m) => m.id === id)?.label ?? id;
  }

  /** One-line fallback-route summary for the collapsed row, e.g.
   *  "→ Codex · gpt-5" or "no fallback". */
  fallbackSummary(cliType: CliType): string {
    const route = this.routes()[cliType];
    const model = route?.fallbackModel;
    if (!model) {
      const catalogueCount = this.catalogueRouteCount(cliType);
      return catalogueCount > 0
        ? `catalogue-derived · ${catalogueCount} equivalence tier${catalogueCount === 1 ? '' : 's'}`
        : 'no fallback';
    }
    const targetCli = (route?.fallbackCliType as CliType | null) ?? cliType;
    const label = this.catalog.modelsFor(targetCli).find((m) => m.id === model)?.label ?? model;
    return `→ ${cliTypeLabel(targetCli)} · ${label}`;
  }

  hasFallback(cliType: CliType): boolean {
    return !!this.routes()[cliType]?.fallbackModel || this.catalogueRouteCount(cliType) > 0;
  }

  /** Catalogue routes remain read-only until an operator deliberately saves an override. */
  catalogueRouteCount(cliType: CliType): number {
    if (this.hasOperatorOverride(cliType)) return 0;
    return this.catalogueRouteCountForFamily(cliType);
  }

  catalogueRouteHelp(cliType: CliType): string {
    const count = this.catalogueRouteCount(cliType);
    if (count === 0) return '';
    return `Catalogue-derived by equivalence tier · ${count} eligible route${count === 1 ? '' : 's'}. `
      + 'Changes in this editor create an explicit operator override.';
  }

  activeFallback(cliType: CliType): CliQuotaActiveFallback | null {
    return this.routes()[cliType]?.activeFallback ?? null;
  }

  activeFallbackSummary(cliType: CliType): string {
    const fallback = this.activeFallback(cliType);
    if (!fallback) return '';
    return `${this.routeEndpoint(fallback.requestedCliType, fallback.requestedModel,
      fallback.requestedThinkingLevel)} → ${this.routeEndpoint(fallback.effectiveCliType,
      fallback.effectiveModel, fallback.effectiveThinkingLevel)}`;
  }

  activeFallbackReason(cliType: CliType): string {
    const fallback = this.activeFallback(cliType);
    return fallback?.reason?.trim() || `${this.cliLabel(cliType)} quota is exhausted.`;
  }

  activeFallbackSince(cliType: CliType): string {
    const activatedAt = this.activeFallback(cliType)?.activatedAt;
    return activatedAt ? formatCompactDateTime(activatedAt) : '';
  }

  activeFallbackReset(cliType: CliType): string {
    const resetAt = this.activeFallback(cliType)?.resetAt;
    return resetAt ? formatCompactDateTime(resetAt) : '';
  }

  routeSourceLabel(cliType: CliType): string {
    const route = this.routes()[cliType];
    const source = (route?.activeFallback?.routeSource ?? route?.routeSource)?.trim().toLowerCase();
    if (!source) {
      if (!route) return '';
      return this.configuredRouteSource(cliType) === 'operator-override'
        ? 'operator override'
        : 'catalogue-derived';
    }
    if (source === 'catalogue' || source === 'catalogue-derived' || source === 'derived') {
      return 'catalogue-derived';
    }
    if (source === 'operator' || source === 'operator-override' || source === 'override') {
      return 'operator override';
    }
    return source.replaceAll('-', ' ');
  }

  fallbackCli(cliType: CliType): CliType {
    return (this.routes()[cliType]?.fallbackCliType as CliType | null) ?? cliType;
  }

  fallbackModels(cliType: CliType): readonly CliModelInfo[] {
    return this.catalog.modelsFor(this.fallbackCli(cliType));
  }

  setPrimary(cliType: CliType, primaryModel: string): void {
    this.save(cliType, {
      primaryModel: primaryModel || null,
      routeSource: this.configuredRouteSource(cliType),
    });
  }

  setFallbackCli(cliType: CliType, fallbackCliType: string): void {
    const target = fallbackCliType as CliType;
    this.catalog.ensure(target).subscribe({ error: () => void 0 });
    this.save(cliType, {
      fallbackCliType: target,
      fallbackModel: null,
      fallbackThinkingLevel: null,
      routeSource: 'operator-override',
    });
  }

  setFallbackModel(cliType: CliType, fallbackModel: string): void {
    this.save(cliType, {
      fallbackModel: fallbackModel || null,
      routeSource: 'operator-override',
    });
  }

  setFallbackThinking(cliType: CliType, fallbackThinkingLevel: string): void {
    this.save(cliType, {
      fallbackThinkingLevel: fallbackThinkingLevel || null,
      routeSource: 'operator-override',
    });
  }

  hasOperatorOverride(cliType: CliType): boolean {
    return this.configuredRouteSource(cliType) === 'operator-override';
  }

  canUseCatalogueFallback(cliType: CliType): boolean {
    return this.hasOperatorOverride(cliType) && this.catalogueRouteCountForFamily(cliType) > 0;
  }

  useCatalogueFallback(cliType: CliType): void {
    this.save(cliType, {
      fallbackCliType: null,
      fallbackModel: null,
      fallbackThinkingLevel: null,
      routeSource: 'catalogue',
    });
  }

  fallbackThinkingLevels(cliType: CliType): readonly string[] {
    const selected = this.routes()[cliType]?.fallbackModel;
    return this.fallbackModels(cliType).find((m) => m.id === selected)?.thinkingLevels ?? [];
  }

  setEconomyMode(enabled: boolean): void {
    const current = this.policy();
    if (!current || this.savingEconomyMode()) return;
    this.policy.set({ ...current, economyMode: enabled });
    this.savingEconomyMode.set(true);
    this.routesApi.setModelRoutingEconomyMode(enabled).subscribe({
      next: (state) => {
        const latest = this.policy();
        if (latest) this.policy.set({ ...latest, economyMode: state.economyMode });
        this.savingEconomyMode.set(false);
      },
      error: () => {
        const latest = this.policy();
        if (latest) this.policy.set({ ...latest, economyMode: current.economyMode });
        this.savingEconomyMode.set(false);
      },
    });
  }

  private save(cliType: CliType, changes: Partial<CliModelRouteProfile>): void {
    const existing = this.routes()[cliType];
    const profile: CliModelRouteProfile = {
      cliType,
      primaryModel: existing?.primaryModel ?? (this.primaryModel(cliType) || null),
      primaryThinkingLevel: existing?.primaryThinkingLevel ?? null,
      fallbackCliType: existing?.fallbackCliType ?? cliType,
      fallbackModel: existing?.fallbackModel ?? null,
      fallbackThinkingLevel: existing?.fallbackThinkingLevel ?? null,
      routeSource: existing?.routeSource ?? null,
      ...changes,
    };
    this.routes.update((all) => ({ ...all, [cliType]: profile }));
    this.savingCli.set(cliType);
    this.routesApi.setModelRoute(profile).subscribe({
      next: (saved) => {
        this.routes.update((all) => ({ ...all, [cliType]: saved }));
        this.savingCli.set(null);
      },
      error: () => this.savingCli.set(null),
    });
  }

  private loadRoutes(): void {
    this.routesApi.getModelRoutes().subscribe({
      next: (response) => {
        this.routes.set(response.profiles ?? {});
        this.catalogueRoutes.set(response.catalogueRoutes ?? []);
      },
      error: () => { /* retain the last known route state */ },
    });
  }

  private routeEndpoint(cliType: string, model: string | null, thinkingLevel: string | null): string {
    const label = this.cliLabel(cliType);
    const modelLabel = model
      ? this.catalog.modelsFor(cliType as CliType).find((item) => item.id === model)?.label ?? model
      : 'default model';
    return [label, modelLabel, thinkingLevel].filter(Boolean).join(' · ');
  }

  private cliLabel(cliType: string): string {
    return (CLI_TYPES as readonly string[]).includes(cliType)
      ? cliTypeLabel(cliType as CliType)
      : cliType;
  }

  private configuredRouteSource(cliType: CliType): string {
    const route = this.routes()[cliType];
    const source = route?.routeSource?.trim().toLowerCase();
    if (source === 'operator' || source === 'operator-override' || source === 'override') {
      return 'operator-override';
    }
    if (source === 'catalogue' || source === 'catalogue-derived' || source === 'derived') {
      return 'catalogue';
    }
    return route?.fallbackModel ? 'operator-override' : 'catalogue';
  }

  private catalogueRouteCountForFamily(cliType: CliType): number {
    return this.catalogueRoutes().filter((route) => route.primaryCliType === cliType).length;
  }
}
