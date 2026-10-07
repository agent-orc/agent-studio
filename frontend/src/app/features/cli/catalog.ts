/**
 * Cycle-safe public API for the per-CLI model catalog cache (AGT-2903). Shared
 * components import this entry instead of the full CLI barrel, which
 * re-exports CLI components.
 */
export { CliCatalogStore } from './services/cli-catalog.store';
