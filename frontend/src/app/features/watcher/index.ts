/** Global Orchestrator Watcher feature public API (orchestrator-waechter dossier §10). */
export { WatcherApiService } from './services/watcher-api.service';
export { WatcherContingentStripComponent } from './components/watcher-contingent-strip/watcher-contingent-strip';
export { WatcherDecisionPanelComponent } from './components/watcher-decision-panel/watcher-decision-panel';
export type { WatcherDecisionEntry } from './components/watcher-decision-panel/watcher-decision-panel';
export type {
  WatcherDetectorClass,
  WatcherCaseState,
  WatcherCase,
  WatcherProposalOutcome,
  WatcherProposalDecision,
  WatcherProposal,
  WatcherProposalDecisionRequest,
  WatcherContingentUsage,
  WatcherContingentBudgets,
  WatcherContingentSnapshot,
  WatcherRunSnapshot,
  WatcherStatusResponse,
} from './models/watcher.model';
