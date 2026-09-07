import type { TaskInfo } from '../../../../models/task.model';
import { cliTypeLabel, formatDateTime } from '../../../../services/format.util';
import { buildThinkingLevelIndicator, type ThinkingLevelIndicator } from '../../../../services/thinking-level.util';

export type HeaderModelSource = 'fallback' | 'run' | 'explicit';

export function resolveHeaderModel(info: TaskInfo): string | null {
  if (info.quotaFallback) return info.quotaFallback.model ?? null;
  return info.execution?.model ?? info.model ?? null;
}

export function resolveHeaderCliType(info: TaskInfo): string | null {
  if (info.quotaFallback) return info.quotaFallback.cliType.trim() || null;
  return info.cliType;
}

export function resolveHeaderModelSource(info: TaskInfo): HeaderModelSource {
  if (info.quotaFallback) return 'fallback';
  return info.execution?.model ? 'run' : 'explicit';
}

export function resolveHeaderThinkingLevel(
  info: TaskInfo,
  defaultThinkingLevel: string | null,
): ThinkingLevelIndicator | null {
  const fallback = info.quotaFallback;
  if (!fallback) {
    return buildThinkingLevelIndicator(
      info.execution,
      info.thinkingLevel,
      defaultThinkingLevel,
      resolveHeaderModel(info),
    );
  }

  // The fallback marker is the resolved launch spec. An older execution must
  // not leak its thinking level into the active fallback presentation.
  const effective = normalizeThinkingLevel(fallback.thinkingLevel);
  if (!effective) return null;
  const configured = normalizeThinkingLevel(fallback.primaryThinkingLevel ?? info.thinkingLevel);
  const defaultLevel = normalizeThinkingLevel(defaultThinkingLevel);
  return {
    short: effective.charAt(0),
    effective,
    configured,
    defaultLevel,
    differsFromConfigured: configured !== null && effective !== configured,
    differsFromDefault: defaultLevel !== null && effective !== defaultLevel,
    tooltip: `Effective thinking level: ${effective}`,
  };
}

export function buildHeaderModelTooltip(
  info: TaskInfo,
  model: string | null,
  cliType: string | null,
  thinkingLevel: ThinkingLevelIndicator | null,
): string {
  const fallback = info.quotaFallback;
  const lines = [
    `Model ID: ${model ?? 'Not set'}`,
    `Thinking level: ${thinkingLevel?.effective ?? 'CLI default'}`,
    `CLI: ${formatCliType(cliType)}`,
  ];

  if (fallback) {
    lines.push(
      'Quota fallback active',
      `Original model ID: ${fallback.primaryModel ?? info.model ?? 'Not set'}`,
      `Original thinking level: ${fallback.primaryThinkingLevel ?? info.thinkingLevel ?? 'CLI default'}`,
      `Original CLI: ${formatCliType(fallback.primaryCliType ?? info.cliType)}`,
      `Reason: ${fallback.reason?.trim() || 'Quota cap reached'}`,
    );
    if (fallback.startedAt) lines.push(`Active since: ${formatDateTime(fallback.startedAt)}`);
    if (fallback.resetAt) lines.push(`Provider reset: ${formatDateTime(fallback.resetAt)}`);
  }

  return lines.join('\n');
}

function normalizeThinkingLevel(value: string | null | undefined): string | null {
  const normalized = value?.trim().toLowerCase();
  return normalized || null;
}

function formatCliType(value: string | null | undefined): string {
  const normalized = value?.trim().toLowerCase();
  switch (normalized) {
    case 'claude': return cliTypeLabel('claude');
    case 'codex': return cliTypeLabel('codex');
    case 'gemini': return cliTypeLabel('gemini');
    default: return value?.trim() || 'Not set';
  }
}
