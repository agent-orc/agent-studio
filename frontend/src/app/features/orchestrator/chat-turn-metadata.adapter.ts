import { dependencies } from '../../../../package.json';
import type { ChatTurnMetadata } from './models/orchestrator.model';

/** Local mirror of the CAC-26 metadata contract until coding-agent-chat 0.5.0 is pinned. */
export interface TurnMetadataCapabilities {
  model: boolean;
  tokens: boolean;
  cost: boolean;
  latency: boolean;
  providerSessionId: boolean;
}

export interface TurnMetadata {
  model?: string;
  effort?: string;
  inputTokens?: number;
  cachedInputTokens?: number;
  outputTokens?: number;
  reasoningTokens?: number;
  cacheCreationTokens?: number;
  cost?: number;
  currency?: string;
  queueMs?: number;
  durationMs?: number;
  providerSessionId?: string;
  executingHost?: string;
  capabilities: TurnMetadataCapabilities;
}

export function supportsTurnMetadata(version: string): boolean {
  const match = /^(\d+)\.(\d+)\.(\d+)/.exec(version);
  return !!match && (Number(match[1]) > 0
    || Number(match[2]) > 5
    || Number(match[2]) === 5 && Number(match[3]) >= 0);
}

/** The event binding activates automatically when the package pin reaches 0.5.0. */
export const CHAT_METADATA_LIBRARY_ENABLED = supportsTurnMetadata(dependencies['coding-agent-chat']);

function elapsed(start?: string | null, end?: string | null): number | undefined {
  if (!start || !end) return undefined;
  const value = Date.parse(end) - Date.parse(start);
  return Number.isFinite(value) && value >= 0 ? value : undefined;
}

export function toTurnMetadata(source: ChatTurnMetadata | null | undefined): TurnMetadata | null {
  if (!source) return null;
  const cli = source.cliType?.toLowerCase();
  const hasUsage = source.inputTokens != null || source.outputTokens != null;
  const codex = cli === 'codex';
  return {
    model: source.model ?? undefined,
    effort: source.effort ?? undefined,
    inputTokens: source.inputTokens ?? undefined,
    cachedInputTokens: source.cachedInputTokens ?? undefined,
    outputTokens: source.outputTokens ?? undefined,
    reasoningTokens: source.reasoningTokens ?? undefined,
    cacheCreationTokens: source.cacheCreationTokens ?? undefined,
    cost: source.cost ?? undefined,
    currency: source.currency ?? undefined,
    queueMs: elapsed(source.queuedAt, source.startedAt),
    durationMs: elapsed(source.startedAt, source.finishedAt),
    providerSessionId: source.providerSessionId ?? undefined,
    executingHost: source.executingHost ?? undefined,
    capabilities: {
      model: !!source.model,
      tokens: hasUsage,
      cost: source.cost != null,
      latency: !!source.startedAt && !!source.finishedAt,
      providerSessionId: codex && !!source.providerSessionId,
    },
  };
}

export function summarizeChatMetadata(turns: readonly { metadata?: ChatTurnMetadata | null }[]): string | null {
  const receipts = turns.flatMap(turn => turn.metadata ? [turn.metadata] : []);
  if (receipts.length === 0) return null;
  const latest = receipts[receipts.length - 1];
  const tokens = receipts.reduce((sum, item) => sum + (item.inputTokens ?? 0)
    + (item.cachedInputTokens ?? 0) + (item.outputTokens ?? 0)
    + (item.cacheCreationTokens ?? 0), 0);
  const cost = receipts.reduce((sum, item) => sum + (item.cost ?? 0), 0);
  const durationMs = receipts.reduce((sum, item) => sum
    + (elapsed(item.queuedAt, item.finishedAt) ?? 0), 0);
  const parts = [latest.model, tokens > 0 ? `${tokens.toLocaleString('en-US')} tokens` : null,
    receipts.every(item => item.cost != null) ? `$${cost.toFixed(4)}` : null,
    durationMs > 0 ? `${Math.round(durationMs / 1000)}s` : null];
  return parts.filter(Boolean).join(' · ');
}
