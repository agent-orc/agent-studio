/**
 * Cycle 9 orchestrator feature models. Lifted out of `models/job.model.ts`
 * per ADR-0034. Re-exported from the legacy file so existing imports
 * keep working; new code should import from this feature folder.
 *
 * Covers the per-project orchestrator log feed, the long-lived
 * orchestrator session, and the orchestrator chat (manager-style
 * conversation alongside the agent runs).
 */

/**
 * One entry in the orchestrator log feed for a project. Mirrors backend
 * `OrchestratorLogEntry`. Kinds: decision / action / observation /
 * intervention. Topics group entries in the UI feed.
 */
export interface OrchestratorLogEntry {
  /** Present on workspace-wide feed responses. */
  project?: string;
  watchPath?: string;
  ts: string;
  kind: 'alert' | 'decision' | 'action' | 'observation' | 'intervention';
  topic: string;
  summary: string;
  reasoning?: string | null;
  jobId?: string | null;
  tokenUsage?: OrchestratorTokenUsage | null;
  userOverride?: { at: string; newDirection: string } | null;
}

export interface OrchestratorTokenUsage {
  model?: string | null;
  /** Optional AGT-2055 attribution; older event writers omit it. */
  thinkingLevel?: string | null;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
}

export interface OrchestratorLogResponse {
  project: string;
  entries: OrchestratorLogEntry[];
}

export interface GlobalOrchestratorFeedResponse {
  entries: OrchestratorLogEntry[];
}

export interface OrchestratorSession {
  sessionId: string;
  model: string;
  bootedAt: string;
  bootPromptPreview: string;
  bootReplyPreview: string;
  cumulativeInputTokens: number;
  cumulativeOutputTokens: number;
  cumulativeCacheReadTokens: number;
  cumulativeCacheCreationTokens: number;
  calls: number;
  lastUsedAt: string;
  lastError?: string | null;
}

export interface OrchestratorSessionResponse {
  project: string;
  session: OrchestratorSession | null;
}

export interface OrchestratorContextSession {
  contextKey: string;
  kind: 'global' | 'project' | 'task';
  projectId: string | null;
  taskKey: string | null;
  createdAt?: string;
  updatedAt: string;
  lastUsedAt?: string;
  model: string | null;
  cumulativeInputTokens: number;
  cumulativeOutputTokens: number;
  cumulativeCacheReadTokens: number;
  cumulativeCacheCreationTokens: number;
  runtimeStatus: 'idle' | 'active' | 'queued' | 'parked';
  queuePosition: number;
  calls?: number;
  hiddenAt?: string | null;
  /** Short Task Server-owned preview of the latest user intent. */
  summary?: string | null;
}

export interface OrchestratorContextSessionsResponse {
  sessions: OrchestratorContextSession[];
}

export type OrchestratorContextDigestSourceName =
  | 'lanes'
  | 'transitions'
  | 'runs'
  | 'quota'
  | 'publishTargets'
  | 'health'
  | 'decisionJournal';

/** One source used to assemble the compact ORCH-1 read-context digest. */
export interface OrchestratorContextDigestSource {
  name: OrchestratorContextDigestSourceName;
  status: 'ok' | 'empty' | 'degraded' | 'unavailable';
  capturedAt: string | null;
  detail: string | null;
}

/**
 * Compact, context-keyed application snapshot supplied to the orchestrator.
 * The backend assembles this from canonical board, run, quota, publishing,
 * health, and decision-journal sources so the frontend never has to merge a
 * potentially inconsistent prompt from its local stores.
 */
export interface OrchestratorContextDigest {
  contextKey: string;
  capturedAt: string;
  digest: string;
  sources: OrchestratorContextDigestSource[];
}

/**
 * One turn in the per-project orchestrator chat. Mirrors backend
 * `OrchestratorChatTurn`. Roles: 'user' for the human's messages,
 * 'orchestrator' for the model's replies. `errorMessage` is set on a
 * failed orchestrator turn so the UI can surface what went wrong without
 * losing the user's text.
 */
export interface OrchestratorChatTurn {
  id: string;
  ts: string;
  role: 'user' | 'orchestrator';
  text: string;
  model?: string | null;
  cliType?: string | null;
  configuredModel?: string | null;
  quotaFallbackReason?: string | null;
  tokenUsage?: OrchestratorTokenUsage | null;
  errorMessage?: string | null;
  contextReceipt?: OrchestratorContextReceipt | null;
  attachments?: OrchestratorChatAttachment[] | null;
}

/** Context blocks the backend composed into one orchestrator reply request. */
export interface OrchestratorContextReceipt {
  scope: 'project' | 'task' | string;
  contextKey: string;
  taskKey?: string | null;
  includedBlocks: string[];
  capturedAt: string;
  receiptId?: string | null;
  userTurnId?: string | null;
  budget?: OrchestratorContextBudgetReceipt | null;
  sources?: OrchestratorContextSourceReceipt[] | null;
}

export interface OrchestratorContextBudgetReceipt {
  automaticSoftCapTokens: number;
  automaticHardCapTokens: number;
  totalHardCapTokens: number;
  estimatedIncludedTokens: number;
}

export interface OrchestratorContextSourceReceipt {
  sourceId: string;
  kind: string;
  revision?: string | null;
  sha256?: string | null;
  freshness: string;
  includedCharacters: number;
  estimatedTokens: number;
  status: 'included' | 'excerpted' | 'unresolved' | 'unavailable' | 'blocked' | 'oversize' | string;
  reason?: string | null;
}

export interface OrchestratorConversationScope {
  kind: 'project' | 'task';
  contextKey: string;
  projectId: string;
  taskKey?: string | null;
}

export interface OrchestratorActiveSurface {
  kind: string;
  reference?: string | null;
  title?: string | null;
  revision?: string | null;
  taskKey?: string | null;
  selection?: string[] | null;
}

export interface OrchestratorContextReference {
  kind: 'task' | 'page' | 'repository-file' | 'commit';
  reference: string;
  projectId?: string | null;
  revision?: string | null;
}

export interface OrchestratorContextBudget {
  automaticSoftCapTokens: number;
  automaticHardCapTokens: number;
  totalHardCapTokens: number;
  charactersPerEstimatedToken: number;
}

/** Immutable context snapshot captured before a chat POST starts. */
export interface OrchestratorContextEnvelope {
  scope: OrchestratorConversationScope;
  activeSurface?: OrchestratorActiveSurface | null;
  explicitReferences: OrchestratorContextReference[];
  budget: OrchestratorContextBudget;
  capturedAt: string;
}

export interface OrchestratorChatAttachment {
  alt: string;
  relativePath: string;
  /**
   * Base64-encoded image bytes for the multimodal fast path. When set, the
   * backend hands the bytes straight to Claude as an image content block in
   * the same user message as the text - no Read tool call needed. Optional;
   * the field is dropped before the turn is persisted so the audit log
   * stays text-only.
   */
  inlineBase64?: string | null;
  /** MIME type of {@link inlineBase64}, e.g. `image/png`. */
  mimeType?: string | null;
}

export interface OrchestratorChatResponse {
  project: string;
  turns: OrchestratorChatTurn[];
  executionContext?: ChatExecutionContext | null;
}

export interface ChatExecutionContext {
  executionKind: 'local' | 'remote';
  hostName: string;
  repoPath?: string | null;
  branch?: string | null;
  headSha?: string | null;
  state: 'ready' | 'resolving' | string;
  capturedAt: string;
}

/**
 * Navigation context the frontend sends with every project-chat POST so the
 * orchestrator can answer context-dependent questions ("what is the current
 * task?", "explain this") against the page the operator is actually on.
 *
 * Background: before this field existed the chat agent answered context
 * questions in vacuum and hallucinated freely (2026-05-09 "Conversation,
 * Foul Conversation" incident). Every field is optional; the backend treats
 * a missing `currentTaskId` as "no task in scope" and the agent must not
 * invent one.
 */
export interface ChatNavigationContext {
  currentPage?: string | null;
  currentTaskId?: string | null;
  currentTaskKey?: string | null;
  currentTaskTitle?: string | null;
  currentTaskState?: string | null;
  currentLaneFilter?: string | null;
  viewportTimestamp?: string | null;
  observedSurface?: string | null;
  affectedComponent?: string | null;
  pageRef?: string | null;
  pageTitle?: string | null;
  pageType?: string | null;
  pageExcerpt?: string | null;
}
