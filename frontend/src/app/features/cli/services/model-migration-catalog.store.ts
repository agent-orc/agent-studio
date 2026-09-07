import { Injectable, computed, inject, signal } from '@angular/core';
import { TaskService } from '../../../services/task.service';
import type { ModelMigrationCatalog, ModelMigrationEntry } from '../models/model-migration.model';

/**
 * Process-wide cache of the model-family migration catalog
 * (`GET /api/cli/model-migrations`), modeled on {@link CliCatalogStore}.
 *
 * Hydrated once on first injection so every consumer (board card, task-detail
 * header, project pipeline settings, CLI Management) shares one fetch instead
 * of issuing its own request. `refresh()` is the explicit escape hatch for
 * surfaces that want to force a re-fetch (e.g. after an admin edits the
 * catalog on disk). `proposalFor` is the single lookup helper every consumer
 * should use instead of hardcoding a specific migration id.
 */
@Injectable({ providedIn: 'root' })
export class ModelMigrationCatalogStore {
  private readonly tasks = inject(TaskService);
  private readonly catalog = signal<ModelMigrationCatalog | null>(null);

  /** Catalog version string, e.g. for a "Migration catalog v3" label. Empty until loaded. */
  readonly version = computed(() => this.catalog()?.version ?? '');
  /** Wiki path documenting the catalog, or null when not yet loaded. */
  readonly wikiPath = computed(() => this.catalog()?.wikiPath ?? null);
  /** Every known migration entry. Consumers should prefer `proposalFor`. */
  readonly entries = computed<readonly ModelMigrationEntry[]>(() => this.catalog()?.migrations ?? []);

  constructor() {
    this.refresh();
  }

  /** Force a re-fetch of the catalog. Safe to call repeatedly. */
  refresh(): void {
    this.tasks.getModelMigrationCatalog().subscribe({
      next: (catalog) => this.catalog.set(catalog),
      error: () => { /* keep the last-known catalog; badges just stay hidden until a retry succeeds */ },
    });
  }

  /**
   * Returns the migration entry whose `from` matches `modelId`
   * (case-insensitive), or null when the model has no known migration —
   * including when the catalog has not loaded yet.
   */
  proposalFor(modelId: string | null | undefined): ModelMigrationEntry | null {
    const needle = (modelId ?? '').trim().toLowerCase();
    if (!needle) return null;
    return this.entries().find((entry) => entry.from.toLowerCase() === needle) ?? null;
  }
}
