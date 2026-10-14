/** Orchestrator feature public API. Cycle 9h / ADR-0034. */
export { OrchestratorFeedComponent } from './components/orchestrator-feed/orchestrator-feed';
export { GlobalOrchestratorCardComponent } from './components/global-orchestrator-card/global-orchestrator-card';
export { LoadDistributionComponent } from './components/load-distribution/load-distribution.component';
export { OrchestratorSideSheetComponent } from './components/orchestrator-side-sheet/orchestrator-side-sheet.component';
export { ChatSessionsPanelComponent } from './components/chat-sessions-panel/chat-sessions-panel.component';
export { OrchestratorChatSessionsStore } from './state/orchestrator-chat-sessions.store';
export { OrchestratorContextHeaderComponent } from './components/orchestrator-context-header/orchestrator-context-header.component';
export { OrchestratorContextReceiptComponent } from './components/orchestrator-context-receipt/orchestrator-context-receipt.component';
export { OrchestratorProjectPickerComponent } from './components/orchestrator-project-picker/orchestrator-project-picker.component';
export { OrchestratorFeedStore } from './state/orchestrator-feed.store';
// AGT-1812: the standalone OrchestratorSettingsModalComponent was retired; its
// content (the platform-global lifecycle flags) now renders as the "Orchestrator"
// section of the consolidated Settings view, so the panel itself is the export.
export { OrchestratorLogicPanelComponent } from './components/orchestrator-logic-panel/orchestrator-logic-panel.component';
export { TransitionRolloutPanelComponent } from './components/transition-rollout-panel/transition-rollout-panel.component';
export { PromptAdminPanelComponent } from './components/prompt-admin-panel/prompt-admin-panel.component';
export type {
  OrchestratorLogEntry,
  OrchestratorTokenUsage,
  OrchestratorLogResponse,
  GlobalOrchestratorFeedResponse,
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
