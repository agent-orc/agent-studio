import type { FileGenerationMeta } from '../../../models/task.model';

export interface GeneratedFileProvenanceView {
  /** Compact one-line summary, kept stable for back-compat callers. */
  label: string;
  tooltip: string;
  /** "<cli> / <model>" (or "generated" when neither is known). */
  producer: string;
  model: string | null;
  cli: string | null;
  /** Pre-formatted "Nk tokens" chip, or null when not recorded. */
  tokens: string | null;
  /** Pre-formatted duration chip (e.g. "2s"), or null when not recorded. */
  duration: string | null;
}

export function generatedFileProvenance(meta: FileGenerationMeta | null | undefined): GeneratedFileProvenanceView | null {
  if (!meta) return null;

  const cliModel = [meta.cli, meta.model].filter(Boolean).join(' / ') || 'generated';
  const tokens = meta.tokensTotal > 0 ? `${formatCount(meta.tokensTotal)} tokens` : null;
  const duration = meta.durationMs > 0 ? formatDuration(meta.durationMs) : null;
  const shortParts = [cliModel, tokens, duration].filter((v): v is string => !!v);

  const tooltipParts = [
    `File: ${meta.file || 'unknown'}`,
    `Kind: ${meta.kind || 'generated'}`,
    `Producer: ${cliModel}`,
    meta.stepId ? `Step: ${meta.stepId}` : null,
    meta.runIndex != null ? `Run: #${meta.runIndex}` : null,
    meta.startedAt ? `Started: ${formatIso(meta.startedAt)}` : null,
    meta.endedAt ? `Ended: ${formatIso(meta.endedAt)}` : null,
    meta.durationMs > 0 ? `Duration: ${formatDuration(meta.durationMs)}` : null,
    meta.tokensTotal > 0
      ? `Tokens: ${formatCount(meta.tokensTotal)} total (${formatCount(meta.tokensIn)} in, ${formatCount(meta.tokensOut)} out)`
      : 'Tokens: not recorded',
    meta.headShaAfter ? `Commit: ${meta.headShaAfter.slice(0, 12)}` : null,
  ].filter((v): v is string => !!v);

  return {
    label: shortParts.length > 0 ? shortParts.join(' | ') : 'Generated',
    tooltip: tooltipParts.join('\n'),
    producer: cliModel,
    model: meta.model || null,
    cli: meta.cli || null,
    tokens,
    duration,
  };
}

export function formatGeneratedFileTokens(tokens: number | null | undefined): string {
  return tokens && tokens > 0 ? `${formatCount(tokens)} tokens` : 'tokens not recorded';
}

function formatCount(n: number): string {
  if (n >= 1_000_000) return `${trimOneDecimal(n / 1_000_000)}M`;
  if (n >= 1_000) return `${trimOneDecimal(n / 1_000)}k`;
  return Math.max(0, Math.round(n)).toLocaleString('en-US');
}

function trimOneDecimal(n: number): string {
  return n.toFixed(1).replace(/\.0$/, '');
}

function formatDuration(ms: number): string {
  if (ms < 1_000) return `${Math.max(0, Math.round(ms))}ms`;
  const seconds = ms / 1_000;
  if (seconds < 60) return `${trimOneDecimal(seconds)}s`;
  const minutes = Math.floor(seconds / 60);
  const remainder = Math.round(seconds % 60);
  return remainder > 0 ? `${minutes}m ${remainder}s` : `${minutes}m`;
}

function formatIso(value: string): string {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return d.toISOString().replace('T', ' ').slice(0, 19) + 'Z';
}
