/**
 * Global Watcher feature (AGT-2721). Public API of the feature; cross-feature
 * imports must come from this barrel, never from an internal path.
 */
export { WatcherApiService } from './services/watcher-api.service';
export { WatcherContingentPanelComponent } from './components/watcher-contingent-panel/watcher-contingent-panel';
export type { ContingentRow } from './components/watcher-contingent-panel/watcher-contingent-panel';
export type {
  WatcherContingentBudget,
  WatcherContingentSnapshot,
  WatcherContingentUsage,
  WatcherDetectorClass,
  WatcherStatus,
} from './models/watcher.model';
