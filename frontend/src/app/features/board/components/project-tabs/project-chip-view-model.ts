import type { TaskInfo, RunnerStatus } from '../../../../models/task.model';
import type {
  ProjectAutoInfo,
  ProjectRunnerIndicator,
  ProjectTokenChipInfo,
} from './project-tabs.component';
import { buildTokenCostTooltip } from '../../../tokens';

export function buildProjectTokenChip(
  jobs: readonly TaskInfo[],
  name: string,
): ProjectTokenChipInfo | null {
  let totalTokens = 0;
  let inputTokens = 0;
  let outputTokens = 0;
  let cacheReadTokens = 0;
  let cacheCreationTokens = 0;
  let jobsWithTokens = 0;
  let estimatedApiCostUsd = 0;
  let allModelsPriced = true;
  const modelLastSeen = new Map<string, number>();

  for (const job of jobs) {
    if (job.projectName !== name) continue;
    const summary = job.tokenSummary;
    if (!summary || summary.totalTokens <= 0) continue;

    jobsWithTokens++;
    // Slim board cards omit zero-valued token fields.
    totalTokens += summary.totalTokens;
    inputTokens += summary.inputTokens ?? 0;
    outputTokens += summary.outputTokens ?? 0;
    cacheReadTokens += summary.cacheReadTokens ?? 0;
    cacheCreationTokens += summary.cacheCreationTokens ?? 0;
    estimatedApiCostUsd += summary.estimatedApiCostUsd ?? 0;
    // Board cards omit allModelsPriced when every model is priced.
    allModelsPriced = allModelsPriced && summary.allModelsPriced !== false;

    for (const entry of summary.entries ?? []) {
      trackModel(modelLastSeen, entry.model, entry.ts);
    }
    trackModel(modelLastSeen, summary.lastModel, summary.lastUpdate);
  }

  if (totalTokens <= 0 || jobsWithTokens === 0) return null;

  const models = [...modelLastSeen.entries()]
    .sort((a, b) => b[1] - a[1])
    .map(([model]) => model);
  const tooltipParts: string[] = [`Input ${formatTokensCompact(inputTokens)} - Output ${formatTokensCompact(outputTokens)}`];
  if (cacheReadTokens > 0) tooltipParts.push(`Cache read ${formatTokensCompact(cacheReadTokens)}`);
  if (cacheCreationTokens > 0) tooltipParts.push(`Cache write ${formatTokensCompact(cacheCreationTokens)}`);
  tooltipParts.push(`${jobsWithTokens} ${jobsWithTokens === 1 ? 'task' : 'tasks'} with AI activity`);
  if (models.length > 0) tooltipParts.push(`Models: ${models.join(', ')}`);
  tooltipParts.push(buildTokenCostTooltip({
    costUsd: allModelsPriced || estimatedApiCostUsd > 0 ? estimatedApiCostUsd : null,
    priceKnown: allModelsPriced,
  }));

  return {
    totalTokens,
    inputTokens,
    outputTokens,
    cacheReadTokens,
    cacheCreationTokens,
    estimatedApiCostUsd,
    allModelsPriced,
    jobsWithTokens,
    models,
    label: formatTokensCompact(totalTokens),
    tooltip: tooltipParts.join('\n'),
  };
}

export function projectRunnerIndicator(
  status: RunnerStatus,
  name: string,
): ProjectRunnerIndicator | null {
  const runner = status.projects[name];
  if (!runner) return null;
  if (runner.activeJobId) return { icon: '🔵', cls: 'running' };
  if (runner.mode === 'paused') return { icon: '⏸', cls: 'paused' };
  if (runner.mode === 'auto-continuous' || runner.mode === 'auto-single') {
    return { icon: '🟢', cls: 'idle' };
  }
  return null;
}

export function projectAutoInfo(status: RunnerStatus, name: string): ProjectAutoInfo {
  const runner = status.projects[name];
  const mode = runner?.mode ?? 'manual';
  const readyCount = runner?.queuedJobIds.length ?? 0;
  const hasActive = !!runner?.activeJobId;

  if (mode === 'auto-continuous' || mode === 'auto-single') {
    return {
      state: 'on',
      readyCount,
      icon: '🔁',
      label: 'Auto',
      tooltip:
        readyCount > 0
          ? `Auto-pickup is on; the next eligible Ready task starts automatically (${readyCount} waiting for a runner slot). Click to stop after the current task.`
          : 'Auto-pickup is on; the next task moved to Ready will start automatically.',
    };
  }

  if (mode === 'paused' && hasActive) {
    return {
      state: 'stopping',
      readyCount,
      icon: '⏸',
      label: 'Stopping',
      tooltip: 'Auto-pickup stopped; the current task keeps running, but no more tasks will be picked up.',
    };
  }

  return {
    state: 'off',
    readyCount,
    icon: '▶',
    label: 'Auto',
    tooltip:
      readyCount > 0
        ? `Enable auto-pickup; the next eligible Ready task starts automatically (${readyCount} waiting for a runner slot).`
        : 'Enable auto-pickup; the next task moved to Ready will start automatically.',
  };
}

export function formatTokensCompact(n: number): string {
  if (!Number.isFinite(n) || n <= 0) return '0';
  if (n < 1_000) return Math.round(n).toString();
  if (n < 10_000) return (n / 1_000).toFixed(1) + 'k';
  if (n < 1_000_000) return Math.round(n / 1_000) + 'k';
  if (n < 10_000_000) return (n / 1_000_000).toFixed(1) + 'M';
  return Math.round(n / 1_000_000) + 'M';
}

function trackModel(map: Map<string, number>, model: string | null | undefined, iso: string | null | undefined): void {
  const key = (model ?? '').trim();
  if (!key) return;
  const timestamp = Date.parse(iso ?? '') || 0;
  const previous = map.get(key) ?? 0;
  if (timestamp > previous) map.set(key, timestamp);
}
