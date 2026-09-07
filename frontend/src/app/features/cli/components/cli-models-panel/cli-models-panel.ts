import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { CLI_TYPES, type CliType } from '../../../../models/task.model';
import type { CliModelInfo } from '../../models/cli.model';
import { CliCatalogStore } from '../../services/cli-catalog.store';
import { ModelMigrationStore } from '../../services/model-migration.store';
import { ModelMigrationHintComponent } from '../model-migration-hint/model-migration-hint.component';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';
import { QuotaApiService, type CliModelRouteProfile, type ModelRoutingPolicyView } from '../../../quota';

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
  imports: [ModelMigrationHintComponent],
  templateUrl: './cli-models-panel.html',
  styleUrl: './cli-models-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CliModelsPanelComponent implements OnInit {
  private readonly catalog = inject(CliCatalogStore);
  private readonly routesApi = inject(QuotaApiService);
  /** Public so the template can look up a proposal per pinned model. */
  readonly migrations = inject(ModelMigrationStore);
  readonly routes = signal<Record<string, CliModelRouteProfile>>({});
  readonly savingCli = signal<string | null>(null);
  readonly policy = signal<ModelRoutingPolicyView | null>(null);
  readonly savingEconomyMode = signal(false);
  readonly savingAutoApply = signal(false);
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
      next: (response) => this.routes.set(response.profiles ?? {}),
    });
    this.routesApi.getModelRoutingPolicy().subscribe({
      next: (policy) => this.policy.set(policy),
    });
    this.migrations.ensure().subscribe({ error: () => void 0 });
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
    if (!model) return 'no fallback';
    const targetCli = (route?.fallbackCliType as CliType | null) ?? cliType;
    const label = this.catalog.modelsFor(targetCli).find((m) => m.id === model)?.label ?? model;
    return `→ ${cliTypeLabel(targetCli)} · ${label}`;
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
    this.save(cliType, { fallbackModel: fallbackModel || null });
  }

  setFallbackThinking(cliType: CliType, fallbackThinkingLevel: string): void {
    this.save(cliType, { fallbackThinkingLevel: fallbackThinkingLevel || null });
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

  setAutoApply(enabled: boolean): void {
    if (this.savingAutoApply()) return;
    this.savingAutoApply.set(true);
    this.migrations.setAutoApply(enabled).subscribe({
      next: () => this.savingAutoApply.set(false),
      error: () => this.savingAutoApply.set(false),
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
}
