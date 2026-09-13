/**
 * Cycle-safe public API for model-migration state used by shared components.
 *
 * Keep this entry point free of Angular components. The shared migration badge
 * is itself consumed by CLI components, so importing the full CLI barrel from
 * that badge would close a component initialization cycle.
 */
export type { ModelMigrationCatalog, ModelMigrationEntry } from './models/model-migration.model';
export { ModelMigrationCatalogStore } from './services/model-migration-catalog.store';
