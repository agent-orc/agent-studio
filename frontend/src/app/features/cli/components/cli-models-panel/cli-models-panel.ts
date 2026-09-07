import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { CLI_TYPES, type CliType } from '../../../../models/task.model';
import type { CliModelInfo } from '../../models/cli.model';
import { CliCatalogStore } from '../../services/cli-catalog.store';
import { cliTypeIcon, cliTypeLabel, formatDateTime } from '../../../../services/format.util';
import {
  QuotaApiService,
  type ActiveQuotaFallback,
  type CliModelRouteProfile,
  type ModelRoutingPolicyView,
} from '../../../quota';

interface CliModelGroup {
  cliType: CliType;
  label: string;
  icon: string;
  models: readonly CliModelInfo[];
  defaultModel: CliModelInfo | null;
}

type FallbackMode = 'catalogue' | 'override' | 'disabled';

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
export class CliModelsPanelComponent implements OnInit {
  private readonly catalog = inject(CliCatalogStore);
  private readonly routesApi = inject(QuotaApiService);
  readonly routes = signal<Record<string, CliModelRouteProfile>>({});
  readonly activeFallbacks = signal<Record<string, ActiveQuotaFallback>>({});
  readonly savingCli = signal<string | null>(null);
  readonly policy = signal<ModelRoutingPolicyView | null>(null);
  readonly savingEconomyMode = signal(false);
  readonly cliTypes = CLI_TYPES;

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
    this.routesApi.getModelRoutes().subscribe({
      next: (response) => {
        this.routes.set(response.profiles ?? {});
        this.activeFallbacks.set(Object.fromEntries(
          (response.activeFallbacks ?? []).map((fallback) => [
            fallback.primaryCliType.toLowerCase(),
            fallback,
          ]),
        ));
      },
    });
    this.routesApi.getModelRoutingPolicy().subscribe({
      next: (policy) => this.policy.set(policy),
    });
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
    if (route?.fallbackDisabled || route?.fallbackSource === 'disabled') return 'fallback disabled';
    const model = route?.fallbackModel;
    if (!model) return route?.fallbackSource === 'none' ? 'no catalogue equivalent' : 'no fallback';
    const targetCli = (route?.fallbackCliType as CliType | null) ?? cliType;
    const label = this.catalog.modelsFor(targetCli).find((m) => m.id === model)?.label ?? model;
    const source = route?.fallbackSource === 'catalogue' ? 'catalogue' : 'override';
    return `→ ${cliTypeLabel(targetCli)} · ${label} · ${source}`;
  }

  hasFallback(cliType: CliType): boolean {
    const route = this.routes()[cliType];
    return !route?.fallbackDisabled && !!route?.fallbackModel;
  }

  fallbackMode(cliType: CliType): FallbackMode {
    const route = this.routes()[cliType];
    if (route?.fallbackDisabled || route?.fallbackSource === 'disabled') return 'disabled';
    if (route?.fallbackSource === 'override' || (!route?.fallbackSource && route?.fallbackModel)) return 'override';
    return 'catalogue';
  }

  setFallbackMode(cliType: CliType, mode: FallbackMode): void {
    const route = this.routes()[cliType];
    if (mode === 'catalogue') {
      this.save(cliType, {
        fallbackCliType: null,
        fallbackModel: null,
        fallbackThinkingLevel: null,
        fallbackDisabled: false,
        fallbackSource: 'catalogue',
      });
      return;
    }
    if (mode === 'disabled') {
      this.save(cliType, {
        fallbackCliType: null,
        fallbackModel: null,
        fallbackThinkingLevel: null,
        fallbackDisabled: true,
        fallbackSource: 'disabled',
      });
      return;
    }

    const targetCli = this.fallbackCli(cliType);
    const models = this.catalog.modelsFor(targetCli);
    const fallbackModel = route?.fallbackModel
      ?? models.find((model) => model.isDefault)?.id
      ?? models[0]?.id
      ?? null;
    this.save(cliType, {
      fallbackCliType: targetCli,
      fallbackModel,
      fallbackThinkingLevel: route?.fallbackThinkingLevel ?? null,
      fallbackDisabled: false,
      fallbackSource: 'override',
    });
  }

  fallbackCli(cliType: CliType): CliType {
    return (this.routes()[cliType]?.fallbackCliType as CliType | null) ?? cliType;
  }

  fallbackModels(cliType: CliType): readonly CliModelInfo[] {
    return this.catalog.modelsFor(this.fallbackCli(cliType));
  }

  setPrimary(cliType: CliType, primaryModel: string): void {
    this.save(cliType, { primaryModel: primaryModel || null });
  }

  setFallbackCli(cliType: CliType, fallbackCliType: string): void {
    const target = fallbackCliType as CliType;
    this.savingCli.set(cliType);
    this.catalog.ensure(target).subscribe({
      next: (models) => this.save(cliType, {
        fallbackCliType: target,
        fallbackModel: models.find((model) => model.isDefault)?.id ?? models[0]?.id ?? null,
        fallbackThinkingLevel: null,
        fallbackDisabled: false,
        fallbackSource: 'override',
      }),
      error: () => this.savingCli.set(null),
    });
  }

  setFallbackModel(cliType: CliType, fallbackModel: string): void {
    this.save(cliType, {
      fallbackModel: fallbackModel || null,
      fallbackDisabled: false,
      fallbackSource: 'override',
    });
  }

  setFallbackThinking(cliType: CliType, fallbackThinkingLevel: string): void {
    this.save(cliType, {
      fallbackThinkingLevel: fallbackThinkingLevel || null,
      fallbackDisabled: false,
      fallbackSource: 'override',
    });
  }

  fallbackThinkingLevels(cliType: CliType): readonly string[] {
    const selected = this.routes()[cliType]?.fallbackModel;
    return this.fallbackModels(cliType).find((m) => m.id === selected)?.thinkingLevels ?? [];
  }

  activeFallback(cliType: CliType): ActiveQuotaFallback | null {
    return this.activeFallbacks()[cliType] ?? null;
  }

  activeFallbackSummary(fallback: ActiveQuotaFallback): string {
    const primary = this.cliLabel(fallback.primaryCliType);
    const effective = this.cliLabel(fallback.effectiveCliType);
    const model = this.modelLabel(fallback.effectiveCliType, fallback.effectiveModel);
    const thinking = fallback.effectiveThinkingLevel ? ` · ${fallback.effectiveThinkingLevel}` : '';
    return `${primary} → ${effective} · ${model}${thinking}`;
  }

  activeFallbackSince(fallback: ActiveQuotaFallback): string {
    if (!Number.isFinite(Date.parse(fallback.activatedAt))) return fallback.activatedAt;
    return formatDateTime(fallback.activatedAt);
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
      fallbackDisabled: existing?.fallbackDisabled ?? false,
      fallbackSource: existing?.fallbackSource ?? 'catalogue',
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

  private cliLabel(cliType: string): string {
    return CLI_TYPES.includes(cliType as CliType) ? cliTypeLabel(cliType as CliType) : cliType;
  }

  private modelLabel(cliType: string, model: string | null): string {
    if (!model) return 'CLI default';
    if (!CLI_TYPES.includes(cliType as CliType)) return model;
    return this.catalog.modelsFor(cliType as CliType).find((candidate) => candidate.id === model)?.label ?? model;
  }
}
