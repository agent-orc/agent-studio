import type { StatusTone } from '../../components/status-indicator/status-indicator.component';
import type { CapacityQueuedCard, OperatorCapacityResponse } from './models/operator-capacity.model';
import { formatCountdown, instantView, UNKNOWN, type UsageInstantView } from './usage-detail.util';

/**
 * Pure view model of the usage detail capacity section (AGT-3584): Ready cards
 * whose next pickup would fall back, wait or be held, as the selection service
 * answers it now. Values come from the response; a missing one reads `Unknown`.
 */
export interface CapacityCardView {
  key: string;
  cardKey: string;
  project: string;
  title: string | null;
  outcome: string;
  outcomeLabel: string;
  tone: StatusTone;
  /** `claude / claude-opus-5 / high`. */
  configured: string;
  /** The route a fallback launches; `null` for a wait or hold, which keeps the configured route. */
  target: string | null;
  reason: string;
  /** `Returns to configured route` for a fallback, `Retries` for a wait or hold. */
  resetLabel: string;
  reset: UsageInstantView | null;
  countdown: string | null;
  /** Catalog note when the installed CLI does not offer the configured model. */
  notOffered: string | null;
}

export interface CapacityDetailView {
  cards: CapacityCardView[];
  /** `2 would fall back, 1 would wait`; counts equal the visible rows. */
  summary: string;
}

const OUTCOMES: Record<string, { label: string; tone: StatusTone; order: number }> = {
  wait: { label: 'Would wait', tone: 'warning', order: 0 },
  fallback: { label: 'Would fall back', tone: 'info', order: 1 },
  throttle: { label: 'Would be held', tone: 'neutral', order: 2 },
};

/** Answers whether the installed CLI offers a model; `null` when offered or unknown. */
export type NotOfferedLookup = (cliType: string | null, model: string | null) => string | null;

function routeText(cli: string | null, model: string | null, thinking: string | null): string {
  return [cli, model, thinking].map(value => value?.trim() || UNKNOWN).join(' / ');
}

function cardView(card: CapacityQueuedCard, timeZone: string, now: number, notOffered: NotOfferedLookup): CapacityCardView {
  const outcome = OUTCOMES[card.outcome] ?? { label: card.outcome, tone: 'neutral' as StatusTone, order: 3 };
  return {
    key: `${card.project}\u0000${card.cardKey}`,
    cardKey: card.cardKey,
    project: card.project,
    title: card.title?.trim() || null,
    outcome: card.outcome,
    outcomeLabel: outcome.label,
    tone: outcome.tone,
    configured: routeText(card.configuredCli, card.configuredModel, card.configuredThinkingLevel),
    target: card.outcome === 'fallback' ? routeText(card.targetCli, card.targetModel, card.targetThinkingLevel) : null,
    reason: card.reason,
    resetLabel: card.outcome === 'fallback' ? 'Returns to configured route' : 'Retries',
    reset: instantView(card.nextResetAt, timeZone),
    countdown: formatCountdown(card.nextResetAt, now),
    notOffered: notOffered(card.configuredCli, card.configuredModel),
  };
}

export function buildCapacityDetailView(
  response: OperatorCapacityResponse,
  timeZone: string,
  now: number,
  notOffered: NotOfferedLookup,
): CapacityDetailView {
  const cards = (response.queuedCards ?? [])
    .map(card => cardView(card, timeZone, now, notOffered))
    .sort((a, b) => (OUTCOMES[a.outcome]?.order ?? 3) - (OUTCOMES[b.outcome]?.order ?? 3)
      || a.project.localeCompare(b.project) || a.cardKey.localeCompare(b.cardKey));
  const counts = new Map<string, number>();
  for (const card of cards) counts.set(card.outcomeLabel, (counts.get(card.outcomeLabel) ?? 0) + 1);
  const summary = cards.length === 0
    ? 'Every Ready card would run its configured model at the current quota.'
    : [...counts].map(([label, count]) => `${count} ${label.toLowerCase()}`).join(', ');
  return { cards, summary };
}
