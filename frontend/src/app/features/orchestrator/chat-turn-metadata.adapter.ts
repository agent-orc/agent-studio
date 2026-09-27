import frontendPackage from '../../../../package.json';
import type { ChatTurnMetadata, OrchestratorChatTurn } from './models/orchestrator.model';

/** The library metadata inputs become available with CAC 0.5.0. */
export function supportsLibraryTurnMetadata(version: string = frontendPackage.dependencies['coding-agent-chat']): boolean {
  const match = /^(\d+)\.(\d+)\.(\d+)/.exec(version.replace(/^[^\d]*/, ''));
  return match !== null && (Number(match[1]) > 0 || Number(match[2]) > 5
    || (Number(match[2]) === 5 && Number(match[3]) >= 0));
}

/** Local mirror of the CAC-26 metadata input while its package is unpublished. */
export interface LibraryTurnMetadata {
  model?: string;
  effort?: string;
  providerSessionId?: string;
  host?: string;
  queueMs?: number;
  durationMs?: number;
  inputTokens?: number;
  cachedInputTokens?: number;
  outputTokens?: number;
  reasoningTokens?: number;
  cost?: number;
  currency?: string;
  priceCatalogueVersion?: string;
}

export interface LibraryTurnMetadataCapabilities {
  model: boolean;
  tokens: boolean;
  cost: boolean;
  durations: boolean;
  providerSessionId: boolean;
  host: boolean;
}

export function toLibraryTurnMetadata(turn: OrchestratorChatTurn): {
  metadata: LibraryTurnMetadata;
  capabilities: LibraryTurnMetadataCapabilities;
} | null {
  const receipt: ChatTurnMetadata | null | undefined = turn.metadata;
  if (turn.role !== 'orchestrator' || !receipt) return null;
  const cli = receipt.cliType?.toLowerCase();
  const usage = cli === 'codex' || cli === 'claude';
  return {
    metadata: {
      model: receipt.model ?? turn.model ?? undefined,
      effort: receipt.effort ?? undefined,
      providerSessionId: receipt.providerSessionId ?? undefined,
      host: receipt.host ?? undefined,
      queueMs: receipt.queueMs ?? undefined,
      durationMs: receipt.durationMs ?? undefined,
      inputTokens: receipt.inputTokens ?? undefined,
      cachedInputTokens: receipt.cachedInputTokens ?? undefined,
      outputTokens: receipt.outputTokens ?? undefined,
      reasoningTokens: receipt.reasoningTokens ?? undefined,
      cost: receipt.cost ?? undefined,
      currency: receipt.currency ?? undefined,
      priceCatalogueVersion: receipt.priceCatalogueVersion ?? undefined,
    },
    capabilities: {
      model: usage,
      tokens: usage && receipt.inputTokens != null,
      cost: usage && receipt.cost != null,
      durations: true,
      providerSessionId: cli === 'codex' && receipt.providerSessionId != null,
      host: receipt.host != null,
    },
  };
}

export function summarizeChatTurns(turns: readonly OrchestratorChatTurn[]): {
  tokens: number;
  cost: number;
  pricedTurns: number;
  turns: number;
  elapsedMs: number;
  queueMs: number;
  models: string[];
  providerSessionIds: string[];
} {
  const receipts = turns.filter(turn => turn.role === 'orchestrator' && turn.metadata).map(turn => turn.metadata!);
  return {
    tokens: receipts.reduce((sum, item) => sum + (item.inputTokens ?? 0)
      + (item.cachedInputTokens ?? 0) + (item.outputTokens ?? 0), 0),
    cost: receipts.reduce((sum, item) => sum + (item.cost ?? 0), 0),
    pricedTurns: receipts.filter(item => item.cost != null).length,
    turns: receipts.length,
    elapsedMs: receipts.reduce((sum, item) => sum + (item.queueMs ?? 0) + (item.durationMs ?? 0), 0),
    queueMs: receipts.reduce((sum, item) => sum + (item.queueMs ?? 0), 0),
    models: [...new Set(receipts.map(item => item.model).filter((model): model is string => !!model))],
    providerSessionIds: [...new Set(receipts.map(item => item.providerSessionId)
      .filter((id): id is string => !!id))],
  };
}
