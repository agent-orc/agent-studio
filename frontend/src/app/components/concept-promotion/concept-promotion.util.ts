import { laneName } from '../../models/lane-presentation';
import type {
  ConceptDecisionAssumption,
  PromoteConceptResponse,
  PromotedConceptTask,
} from '../../models/task.model';

/** One Dossier implementation slice as the owner reviews it before promotion. */
export interface ConceptPromotionSlice {
  /** Zero-based descriptor index sent back to the promotion endpoint. */
  index: number;
  /** One-based position in the Dossier's implementation order. */
  order: number;
  title: string;
  decision: string;
  section: string | null;
  /** The card a previous promotion created for this slice, if any. */
  card: { key: string; lane: string | null } | null;
}

/**
 * Projects the promotion plan into reviewable rows, one per slice, in Dossier
 * order. A slice that already has a card carries it, so a repeated request
 * never looks like it would create a second one.
 */
export function promotionSlices(
  plan: PromoteConceptResponse,
  created: readonly PromotedConceptTask[] = [],
): ConceptPromotionSlice[] {
  const cards = new Map<number, PromotedConceptTask>();
  for (const card of [...(plan.promoted ?? []), ...created])
    if (card.itemIndex != null) cards.set(card.itemIndex, card);
  const decisions = plan.source.decisions ?? [];
  return plan.items.map((item, index) => {
    const card = cards.get(index);
    return {
      index,
      order: index + 1,
      title: item.title.trim(),
      decision: item.decision?.question
        ? `Decision card: ${item.decision.question}`
        : decisionLabel(item.decisionId ?? null, decisions),
      section: item.section?.trim() || null,
      card: card ? { key: card.taskKey || card.jobId, lane: card.state ?? null } : null,
    };
  });
}

/** The working decision a slice implements: the operator's pick, else the recommendation. */
export function decisionLabel(
  decisionId: string | null,
  decisions: readonly ConceptDecisionAssumption[],
): string {
  if (!decisionId) return 'No linked decision';
  const working = decisions.find(decision => decision.id === decisionId && decision.isWorkingAssumption);
  if (!working) return decisionId;
  const source = working.operatorSelected ? 'operator choice' : 'recommended';
  return `${working.label}: ${working.optionLabel} (${source})`;
}

export function cardLabel(card: NonNullable<ConceptPromotionSlice['card']>): string {
  return card.lane ? `${card.key} · ${laneName(card.lane)}` : card.key;
}

export function promotionErrorMessage(error: unknown, fallback: string): string {
  const candidate = error as { error?: { error?: string } | string; message?: string } | null;
  if (typeof candidate?.error === 'string' && candidate.error.trim()) return candidate.error;
  if (candidate?.error && typeof candidate.error === 'object' && candidate.error.error)
    return candidate.error.error;
  return fallback;
}
