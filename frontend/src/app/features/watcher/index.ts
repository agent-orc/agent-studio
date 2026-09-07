/** Global Watcher feature public API (AGT-2721). */
export { WatcherApiService } from './services/watcher-api.service';
export {
  WatcherDecisionComponent,
  isWatcherDecisionEntry,
} from './components/watcher-decision/watcher-decision';
export { WatcherContingentComponent } from './components/watcher-contingent/watcher-contingent';
export type { ContingentRow } from './components/watcher-contingent/watcher-contingent';
export {
  WATCHER_PARTICIPANT_ID,
  WATCHER_TOPICS,
} from './models/watcher.model';
export type {
  WatcherCase,
  WatcherContingentLimits,
  WatcherContingentSnapshot,
  WatcherContingentUsage,
  WatcherDecisionRequest,
  WatcherDecisionState,
  WatcherDetectorClass,
  WatcherEvidenceItem,
  WatcherModelCall,
  WatcherModelRecommendation,
  WatcherProposal,
  WatcherSnapshot,
  WatcherStatus,
  WatcherSuppressionView,
} from './models/watcher.model';
