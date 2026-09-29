import type { StudioChatTurnMetadata } from './models/orchestrator.model';
import manifest from '../../../../package.json';

/** Studio's bridge to CAC-26 until coding-agent-chat 0.5.0 is published. */
export interface LibraryTurnMetadata {
  model?: string;
  effort?: string;
  providerThreadId?: string;
  host?: string;
  inputTokens?: number;
  cachedInputTokens?: number;
  outputTokens?: number;
  reasoningTokens?: number;
  cost?: number;
  currency?: string;
  priceCatalogueVersion?: string;
  queueDurationMs?: number;
  runDurationMs?: number;
}

export interface LibraryTurnMetadataCapabilities {
  tokens: boolean;
  cost: boolean;
  reasoningTokens: boolean;
  queueDuration: boolean;
  runDuration: boolean;
  providerThreadId: boolean;
}

export function chatMetadataLibraryEnabled(version: string = manifest.dependencies['coding-agent-chat']): boolean {
  const match = /^(\d+)\.(\d+)\.(\d+)$/.exec(version);
  return !!match && (Number(match[1]) > 0 || Number(match[2]) > 5
    || (Number(match[2]) === 5 && Number(match[3]) >= 0));
}

export function adaptChatTurnMetadata(source: StudioChatTurnMetadata | null | undefined):
  { metadata: LibraryTurnMetadata; capabilities: LibraryTurnMetadataCapabilities } | null {
  if (!source) return null;
  const queueDurationMs = duration(source.queuedAt, source.startedAt);
  const runDurationMs = duration(source.startedAt, source.finishedAt);
  return {
    metadata: {
      model: source.model ?? undefined,
      effort: source.effort ?? undefined,
      providerThreadId: source.providerThreadId ?? undefined,
      host: source.host ?? undefined,
      inputTokens: source.inputTokens ?? undefined,
      cachedInputTokens: source.cachedInputTokens ?? undefined,
      outputTokens: source.outputTokens ?? undefined,
      reasoningTokens: source.reasoningTokens ?? undefined,
      cost: source.cost ?? undefined,
      currency: source.currency ?? undefined,
      priceCatalogueVersion: source.priceCatalogueVersion ?? undefined,
      queueDurationMs: queueDurationMs ?? undefined,
      runDurationMs: runDurationMs ?? undefined,
    },
    capabilities: {
      tokens: source.inputTokens != null || source.outputTokens != null,
      cost: source.cost != null,
      reasoningTokens: source.reasoningTokens != null,
      queueDuration: queueDurationMs != null,
      runDuration: runDurationMs != null,
      providerThreadId: !!source.providerThreadId,
    },
  };
}

function duration(start: string | null | undefined, finish: string | null | undefined): number | null {
  if (!start || !finish) return null;
  const milliseconds = Date.parse(finish) - Date.parse(start);
  return Number.isFinite(milliseconds) && milliseconds >= 0 ? milliseconds : null;
}
