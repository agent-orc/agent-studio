/** Orchestrator feature public API. Cycle 9h / ADR-0034. */
export { OrchestratorSideSheetHostComponent } from './components/orchestrator-side-sheet-host/orchestrator-side-sheet-host.component';
export { OrchestratorPanelStateService } from './state/orchestrator-panel-state.service';
export { OrchestratorChatSessionsStore } from './state/orchestrator-chat-sessions.store';
export { OrchestratorFeedStore } from './state/orchestrator-feed.store';
// AGT-1812: the standalone OrchestratorSettingsModalComponent was retired; its
// content (the platform-global lifecycle flags) now renders as the "Orchestrator"
// section of the consolidated Settings view, so the panel itself is the export.
export type {
  OrchestratorLogEntry,
  OrchestratorTokenUsage,
  OrchestratorLogResponse,
  GlobalOrchestratorFeedResponse,
  OperationalStallCase,
  OrchestratorSession,
  OrchestratorSessionResponse,
  OrchestratorContextSession,
  OrchestratorContextSessionsResponse,
  OrchestratorContextDigest,
  OrchestratorContextDigestSource,
  OrchestratorContextDigestSourceName,
  OrchestratorChatTurn,
  OrchestratorContextReceipt,
  OrchestratorContextBudgetReceipt,
  OrchestratorContextSourceReceipt,
  OrchestratorConversationScope,
  OrchestratorActiveSurface,
  OrchestratorContextReference,
  OrchestratorContextBudget,
  OrchestratorContextEnvelope,
  OrchestratorChatAttachment,
  OrchestratorChatResponse,
  ChatExecutionContext,
  ChatExecutionReadiness,
  ChatNavigationContext,
} from './models/orchestrator.model';
export type {
  OrchestratorContextSourceCategory,
  OrchestratorContextSourceOption,
} from './models/orchestrator-context-source.model';
export { contextSourceId } from './models/orchestrator-context-source.model';
export { buildChatNavigationContext } from './chat-navigation-context';
export { buildOrchestratorContextEnvelope } from './orchestrator-context-envelope';
export { buildOrchestratorConversationEvents } from './components/orchestrator-side-sheet/orchestrator-side-sheet.util';
export {
  buildContextChipPresentation,
  buildComposerLocationContext,
  composerLocationLabel,
  type ContextChipPresentation,
  type ComposerLocationContext,
} from './composer-location-context';
