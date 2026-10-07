/**
 * Canonical rung order shared by every CLI ladder, weakest first. Mirrors
 * `ModelMetadataRegistry.ThinkingRungOrder` in the backend (codex-cli lists
 * `max` below `ultra`).
 */
const RUNG_ORDER: readonly string[] = ['minimal', 'low', 'medium', 'high', 'xhigh', 'max', 'ultra'];

export interface ThinkingLevelMapping {
  /** The level the run actually uses. */
  level: string | null;
  /** The pinned level the model does not offer, or null when nothing was mapped. */
  mappedFrom: string | null;
}

/**
 * Resolves a pinned thinking level against a model's discovered ladder with
 * the backend's rule (AGT-2903): an offered level wins, a known rung the model
 * does not offer maps to the highest offered rung below it, and anything else
 * lands on the model default. No mapping is hard-coded per model.
 */
export function mapThinkingLevel(
  levels: readonly string[] | null | undefined,
  defaultLevel: string | null | undefined,
  requested: string | null | undefined,
): ThinkingLevelMapping {
  const ladder = levels ?? [];
  const pinned = requested?.trim().toLowerCase() || null;
  if (ladder.length === 0) return { level: null, mappedFrom: null };
  if (pinned === null) return { level: defaultLevel ?? ladder[0] ?? null, mappedFrom: null };

  const exact = ladder.find((level) => level.toLowerCase() === pinned);
  if (exact !== undefined) return { level: exact, mappedFrom: null };

  const pinnedRank = RUNG_ORDER.indexOf(pinned);
  const lower = pinnedRank <= 0
    ? undefined
    : ladder
      .map((level) => ({ level, rank: RUNG_ORDER.indexOf(level.toLowerCase()) }))
      .filter((candidate) => candidate.rank >= 0 && candidate.rank < pinnedRank)
      .sort((left, right) => right.rank - left.rank)[0]?.level;
  return { level: lower ?? defaultLevel ?? ladder[0] ?? null, mappedFrom: pinned };
}

/** Operator-facing sentence for a mapped level, or null when nothing was mapped. */
export function mappedThinkingLevelNote(
  mapping: ThinkingLevelMapping,
  modelId: string | null | undefined,
): string | null {
  if (mapping.mappedFrom === null || mapping.level === null) return null;
  const model = modelId?.trim() || 'this model';
  return `Pinned ${mapping.mappedFrom} is not offered by ${model}; runs at ${mapping.level}.`;
}
