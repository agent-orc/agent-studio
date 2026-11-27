import { FormsModule } from '@angular/forms';
import { browserTimeZone, formatZonedWithUtc } from '../../../../services/usage-time.util';
import { UsageCockpitService } from '../../../usage-cockpit/services/usage-cockpit.service';
import { ChangeDetectionStrategy, Component, OnInit, computed, forwardRef, inject, signal } from '@angular/core';
import { CLI_TYPES, type CliType } from '../../../../models/task.model';
import type { CliModelInfo } from '../../models/cli.model';
import { CliCatalogStore } from '../../services/cli-catalog.store';
import { ModelMigrationCatalogStore } from '../../services/model-migration-catalog.store';
import { ModelMigrationBadgeComponent } from '../../../../components/model-migration-badge/model-migration-badge.component';
import { DisclosureMarkerComponent } from '@studio/ui/components/disclosure-marker/disclosure-marker.component';
import { StatusIndicatorComponent, type StatusTone } from '@studio/ui/components/status-indicator/status-indicator.component';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';
import { QuotaApiService, type CliFallbackState, type CliModelFallbackRoute, type CliModelRouteProfile, type ModelRoutingPolicyView } from '../../../quota';
import type { ActiveModelSwitch, EffectiveModelSelection } from '../../../quota/services/quota-api.service';
import { ConsumerModelPolicyComponent } from '../consumer-model-policy/consumer-model-policy.component';
import { scrollCliModelsTable } from './cli-models-table-keyboard';
import { EconomyRoutingPreviewComponent } from '../economy-routing-preview/economy-routing-preview.component';
import { RoutingCostService, type PricedRouteModel } from '../../services/routing-cost.service';
import { TaskService } from '../../../../services/task.service';
import { WorkspaceOrchestratorSettingsService } from '../../../../services/workspace-orchestrator-settings.service';

interface CliModelGroup {
  cliType: CliType;
  label: string;
  icon: string;
  models: readonly CliModelInfo[];
  defaultModel: CliModelInfo | null;
}

/** Quota fallback state per CLI: any active or operator-preferred fallback needs attention. */
const FALLBACK_STATE_TONES: Readonly<Record<CliFallbackState['state'], StatusTone>> = {
  normal: 'neutral',
  'fallback-active': 'warning',
  'fallback-preferred': 'warning',
};

export function fallbackStateTone(state: string | null | undefined): StatusTone {
  return FALLBACK_STATE_TONES[state as CliFallbackState['state']] ?? 'neutral';
}

/** Cached CLI catalog and optimistic routing edits, serialized independently per CLI. */
@Component({
  selector: 'app-cli-models-panel',
  standalone: true,
  // Defer the migration badge to avoid its CLI barrel evaluation cycle.
  imports: [FormsModule, EconomyRoutingPreviewComponent, forwardRef(() => ModelMigrationBadgeComponent), DisclosureMarkerComponent, ConsumerModelPolicyComponent, StatusIndicatorComponent],
  templateUrl: './cli-models-panel.html',
  styleUrl: './cli-models-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CliModelsPanelComponent implements OnInit {
  private readonly cockpit = inject(UsageCockpitService);
  private readonly catalog = inject(CliCatalogStore);
  private readonly routesApi = inject(QuotaApiService);
  private readonly jobs = inject(TaskService);
  private readonly workspaceOrchestrator = inject(WorkspaceOrchestratorSettingsService);
  readonly migrations = inject(ModelMigrationCatalogStore);
  readonly routes = signal<Partial<Record<CliType, CliModelRouteProfile>>>({});
  readonly fallbackRoutes = signal<CliModelFallbackRoute[]>([]);
  readonly fallbackStates = signal<Record<string, CliFallbackState>>({});
  readonly effectiveRoutes = signal<EffectiveModelSelection[]>([]);
  readonly activeSwitches = signal<ActiveModelSwitch[]>([]);
  readonly catalogueVersion = signal<string | null>(null);
  readonly callersCannotReroute = signal<{ caller: string; cliType: string; reason: string }[]>([]);
  readonly preferenceSaving = signal<string | null>(null);
  readonly savingClis = signal<ReadonlySet<string>>(new Set());
  readonly routeErrors = signal<Partial<Record<CliType, Partial<CliModelRouteProfile>>>>({});
  private readonly queuedChanges = new Map<CliType, Partial<CliModelRouteProfile>>();
  private readonly costs = inject(RoutingCostService);
  readonly pricedModels = signal<PricedRouteModel[]>([]);
  readonly policy = signal<ModelRoutingPolicyView | null>(null);
  readonly savingEconomyMode = signal(false);
  readonly cliTypes = CLI_TYPES;

  /** Default workspace owns automatic model migrations (AGT-2716). */
  readonly workspaceId = signal<string | null>(null);
  /** null while loading; always concrete once the settings GET resolves. */
  readonly autoApplyModelMigrations = signal<boolean | null>(null);
  readonly autoApplySaving = signal(false);

  /** Expanded CLI route editors and model lists. */
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

  /** Distinguish the thinking-level routes emitted for the same source model. */
  fallbackRouteKey(route: CliModelFallbackRoute): string {
    return JSON.stringify([
      route.fromCliType, route.fromModel, route.fromThinkingLevel,
      route.toCliType, route.toModel, route.toThinkingLevel,
    ]);
  }

  ngOnInit(): void {
    this.catalog.hydrateAll();
    this.costs.models().subscribe({ next: response => this.pricedModels.set(response.models), error: () => this.pricedModels.set([]) });
    this.routesApi.getModelRoutes().subscribe({
      next: (response) => {
        this.routes.set(response.profiles ?? {});
        this.fallbackRoutes.set(response.routes ?? []);
        this.fallbackStates.set(response.states ?? {});
        this.catalogueVersion.set(response.catalogueVersion ?? null);
        this.callersCannotReroute.set(response.callersCannotReroute ?? []);
      },
    });
    this.routesApi.getModelRoutingPolicy().subscribe({
      next: (policy) => this.policy.set(policy),
    });
    this.refreshEffectiveRoutes();
    this.routesApi.getActiveModelSwitches().subscribe({
      next: response => this.activeSwitches.set(response.filter(item =>
        (item.kind === 'switched' || item.kind === 'operator-switch') && item.from && item.to)),
    });
    this.loadWorkspaceAutoApply();
  }

  /** Resolve the default workspace and load its migration setting. */
  private loadWorkspaceAutoApply(): void {
    this.jobs.getRegistryWorkspaces({ includeArchived: true }).subscribe({
      next: (workspaces) => {
        const owner = (workspaces ?? []).find((w) => w.isDefault) ?? (workspaces ?? [])[0] ?? null;
        if (!owner) return;
        this.workspaceId.set(owner.id);
        this.workspaceOrchestrator.get(owner.id).subscribe({
          next: (s) => this.autoApplyModelMigrations.set(s.autoApplyModelMigrations),
          error: () => { /* toggle stays hidden behind the loading state */ },
        });
      },
      error: () => { /* no workspace context; toggle stays hidden */ },
    });
  }

  /** Save the workspace automatic-migration setting. */
  setAutoApplyMigrations(enabled: boolean): void {
    const id = this.workspaceId();
    if (!id || this.autoApplySaving()) return;
    const previous = this.autoApplyModelMigrations();
    this.autoApplyModelMigrations.set(enabled);
    this.autoApplySaving.set(true);
    this.workspaceOrchestrator.setAutoApplyModelMigrations(id, enabled).subscribe({
      next: (r) => {
        this.autoApplyModelMigrations.set(r.autoApplyModelMigrations);
        this.autoApplySaving.set(false);
      },
      error: () => {
        this.autoApplyModelMigrations.set(previous);
        this.autoApplySaving.set(false);
      },
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

  readonly onTableScrollKeydown = scrollCliModelsTable;
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
  /** Collapsed fallback summary; catalogue-derived defaults are marked automatic. */
  fallbackSummary(cliType: CliType): string {
    const route = this.routes()[cliType];
    const model = route?.fallbackModel;
    if (!model) return 'no fallback';
    const targetCli = (route?.fallbackCliType as CliType | null) ?? cliType;
    const label = this.catalog.modelsFor(targetCli).find((m) => m.id === model)?.label ?? model;
    const suffix = route?.isFallbackDerived ? ' (auto)' : '';
    return `→ ${cliTypeLabel(targetCli)} · ${label}${suffix}`;
  }
  hasFallback(cliType: CliType): boolean {
    return !!this.routes()[cliType]?.fallbackModel;
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
    this.catalog.ensure(target).subscribe({ error: () => void 0 });
    this.save(cliType, { fallbackCliType: target, fallbackModel: null, fallbackThinkingLevel: null });
  }
  setFallbackModel(cliType: CliType, fallbackModel: string): void {
    this.save(cliType, { fallbackCliType: this.fallbackCli(cliType), fallbackModel: fallbackModel || null, fallbackThinkingLevel: null });
  }
  setFallbackThinking(cliType: CliType, fallbackThinkingLevel: string): void {
    this.save(cliType, { fallbackCliType: this.fallbackCli(cliType), fallbackModel: this.routes()[cliType]?.fallbackModel ?? null, fallbackThinkingLevel: fallbackThinkingLevel || null });
  }
  fallbackThinkingLevels(cliType: CliType): readonly string[] {
    const selected = this.routes()[cliType]?.fallbackModel;
    return this.fallbackModels(cliType).find((m) => m.id === selected)?.thinkingLevels ?? [];
  }

  setPreferFallback(cliType: CliType, active: boolean): void {
    if (this.preferenceSaving()) return;
    this.preferenceSaving.set(cliType);
    this.routesApi.setFallbackPreference(cliType, active).subscribe({
      next: preference => {
        this.fallbackStates.update(states => ({
          ...states,
          [cliType]: {
            ...(states[cliType] ?? { cliType, windows: [] }),
            state: preference.active
              ? 'fallback-preferred'
              : states[cliType]?.windows.some(window => window.usedPct !== null && window.usedPct >= window.capPct)
                ? 'fallback-active'
                : 'normal',
            activeSince: preference.enabledAt,
            preferenceExpiresAt: preference.expiresAt,
          },
        }));
        this.preferenceSaving.set(null);
        this.refreshEffectiveRoutes();
      },
      error: () => this.preferenceSaving.set(null),
    });
  }
  modelPriceLabel(cli: string, model: CliModelInfo): string {
    const price = this.pricedModels().find(option => option.cli === cli && option.model === model.id)?.price;
    return `${model.label} · ${price ? `$${price.inputPerMillion} in / $${price.outputPerMillion} out per MTok` : 'price unknown'}`;
  }
  effectiveFor(cliType: CliType): EffectiveModelSelection[] {
    const seen = new Set<string>();
    return this.effectiveRoutes().filter(route => {
      const key = route.consumer + ':' + (route.stepId ?? '');
      if (route.configured.cliType !== cliType || seen.has(key)) return false;
      seen.add(key);
      return true;
    });
  }

  private refreshEffectiveRoutes(): void {
    this.routesApi.getEffectiveModelSelection().subscribe({
      next: response => this.effectiveRoutes.set(response.routes ?? []),
      error: () => this.effectiveRoutes.set([]),
    });
  }
  routeLabel(model: string, thinking: string | null): string {
    return thinking ? `${model} ${thinking}` : model;
  }
  routeSource(route: CliModelFallbackRoute): string {
    return route.source === 'override' ? 'override' : route.catalogueVersion ?? 'catalogue';
  }
  price(input: number | null, output: number | null): string {
    if (input === null || output === null) return 'price unavailable';
    return `$${input.toFixed(2)} in / $${output.toFixed(2)} out`;
  }

  fallbackStateTone(cliType: CliType): StatusTone {
    return fallbackStateTone(this.fallbackStates()[cliType]?.state);
  }

  fallbackStateLabel(cliType: CliType): string {
    const state = this.fallbackStates()[cliType];
    if (!state) return 'normal';
    if (state.state === 'fallback-active') return `fallback active${state.activeSince ? ` since ${this.shortDate(state.activeSince)}` : ''}`;
    if (state.state === 'fallback-preferred') {
      return `fallback preferred by operator${state.preferenceExpiresAt ? ` until ${this.shortDate(state.preferenceExpiresAt)}` : ''}`;
    }
    return 'normal';
  }

  shortDate(value: string): string {
    return formatZonedWithUtc(value, this.cockpit.snapshot()?.timeZone ?? browserTimeZone()) ?? value;
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
  isSaving(cliType: string): boolean { return this.savingClis().has(cliType); }
  retryRoute(cliType: CliType): void {
    const changes = this.routeErrors()[cliType];
    if (changes && !this.isSaving(cliType)) this.save(cliType, changes);
  }
  private finishSaving(cliType: CliType): void {
    this.savingClis.update(saving => new Set([...saving].filter(cli => cli !== cliType)));
  }

  private save(cliType: CliType, changes: Partial<CliModelRouteProfile>): void {
    // Retain this snapshot for rollback if the edit starts a request.
    const acknowledged = this.routes()[cliType];
    const editsFallback = ['fallbackCliType', 'fallbackModel', 'fallbackThinkingLevel']
      .some(key => Object.hasOwn(changes, key));
    const display: CliModelRouteProfile = {
      ...acknowledged, cliType,
      primaryModel: acknowledged?.primaryModel ?? (this.primaryModel(cliType) || null),
      primaryThinkingLevel: acknowledged?.primaryThinkingLevel ?? null,
      fallbackCliType: acknowledged?.fallbackCliType ?? cliType,
      fallbackModel: acknowledged?.fallbackModel ?? null,
      fallbackThinkingLevel: acknowledged?.fallbackThinkingLevel ?? null,
      ...changes, ...(editsFallback ? { isFallbackDerived: false } : {}),
    };
    this.routes.update(all => ({ ...all, [cliType]: display }));
    this.routeErrors.update(errors => ({ ...errors, [cliType]: undefined }));
    if (this.isSaving(cliType)) {
      this.queuedChanges.set(cliType, { ...this.queuedChanges.get(cliType), ...changes });
      return;
    }
    // Catalogue defaults are display-only until an explicit fallback edit (AGT-2751).
    const fallback = display.isFallbackDerived ? undefined : display;
    const profile: CliModelRouteProfile = {
      cliType, primaryModel: display.primaryModel, primaryThinkingLevel: display.primaryThinkingLevel,
      fallbackCliType: fallback?.fallbackCliType ?? cliType,
      fallbackModel: fallback?.fallbackModel ?? null,
      fallbackThinkingLevel: fallback?.fallbackThinkingLevel ?? null,
    };
    this.savingClis.update(saving => new Set([...saving, cliType]));
    this.routesApi.setModelRoute(profile).subscribe({
      next: saved => {
        this.routes.update(all => ({ ...all, [cliType]: saved }));
        this.finishSaving(cliType);
        const queued = this.queuedChanges.get(cliType);
        this.queuedChanges.delete(cliType);
        if (queued) this.save(cliType, queued);
      },
      error: () => {
        // Reject the optimistic chain, but retain its complete intent for explicit retry.
        const retry = { ...changes, ...this.queuedChanges.get(cliType) };
        this.queuedChanges.delete(cliType);
        this.routes.update(all => ({ ...all, [cliType]: acknowledged }));
        this.routeErrors.update(errors => ({ ...errors, [cliType]: retry }));
        this.finishSaving(cliType);
      },
    });
  }
}
