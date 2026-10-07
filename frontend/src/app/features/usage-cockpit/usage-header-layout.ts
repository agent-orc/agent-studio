import type { UsageCli } from './models/usage-cockpit.model';

/**
 * Pure layout policy for the header usage cockpit (HUC-S4,
 * docs/header-usage-cockpit/index.html "Responsive layout").
 *
 * The tier comes from the header's own width, never the viewport, so a
 * narrow container, a docked side sheet and 200% zoom all follow the same
 * priority rules as the 768 / 1200 / 1600 CSS px breakpoints.
 */
export type HeaderTier = 'phone' | 'tablet' | 'desktop' | 'wide';

const TIER_ORDER: readonly HeaderTier[] = ['phone', 'tablet', 'desktop', 'wide'];

export function headerTierForWidth(width: number): HeaderTier {
  if (width < 768) return 'phone';
  if (width < 1200) return 'tablet';
  if (width < 1600) return 'desktop';
  return 'wide';
}

/** One step narrower (collapse order step 2: navigation moves into More). */
export function narrowerTier(tier: HeaderTier): HeaderTier {
  return TIER_ORDER[Math.max(0, TIER_ORDER.indexOf(tier) - 1)];
}

export function tierAtLeast(tier: HeaderTier, min: HeaderTier): boolean {
  return TIER_ORDER.indexOf(tier) >= TIER_ORDER.indexOf(min);
}

/**
 * Primary order: the operator's current CLI selection, then the saved
 * default, then stable provider order (projection order). Usage spikes never
 * reorder chips.
 */
export function orderClis(
  clis: readonly UsageCli[],
  selectedCliId: string | null,
  defaultCliId: string | null,
): UsageCli[] {
  const priorities = [selectedCliId, defaultCliId]
    .filter((id): id is string => id != null)
    .map(id => id.toLowerCase());
  const ordered: UsageCli[] = [];
  const seen = new Set<string>();

  for (const id of priorities) {
    const match = clis.find(c => c.cliId.toLowerCase() === id);
    if (match && !seen.has(id)) {
      ordered.push(match);
      seen.add(id);
    }
  }
  for (const cli of clis) {
    const id = cli.cliId.toLowerCase();
    if (!seen.has(id)) {
      ordered.push(cli);
      seen.add(id);
    }
  }
  return ordered;
}

/** Established short provider names for the narrowest fit (step 5). */
const CLI_ABBREVIATIONS: Record<string, string> = {
  claude: 'CL', codex: 'CX', copilot: 'CP', gemini: 'GM',
};

export function cliAbbreviation(cliId: string): string {
  return CLI_ABBREVIATIONS[cliId.toLowerCase()] ?? cliId.slice(0, 3).toUpperCase();
}

/**
 * How the visible usage chips read. `full` keeps both windows; `compact`
 * is the phone composition (primary weekly only, cost without the visible
 * USD suffix); `abbreviated` also shortens the provider; `bare` drops the
 * visible WK / Today labels. The accessible name never changes.
 */
export type UsageChipFit = 'full' | 'compact' | 'abbreviated' | 'bare';

/** Measured widths in CSS px of each chip variant and header part. */
export interface UsageHeaderMeasures {
  primary: Record<UsageChipFit, number>;
  cost: Record<UsageChipFit, number>;
  secondaries: readonly number[];
  details: number;
  wordmark: number;
  more: number;
  gap: number;
  gutter: number;
}

export interface UsageHeaderPlan {
  /** `rows`: navigation above usage. `single`: the one-row phone header. */
  layout: 'rows' | 'single';
  fit: UsageChipFit;
  /** Count of secondary CLI chips shown, in stable order; the rest go to Details. */
  visibleSecondaries: number;
  showWordmark: boolean;
  showDetails: boolean;
}

const COMPACT_STEPS: readonly UsageChipFit[] = ['compact', 'abbreviated', 'bare'];

/**
 * Collapse order from the Dossier: (3) move whole secondary CLI chips into
 * Details, last first; (4) fall back to the phone composition; (5) shorten
 * the provider and drop the wordmark, then labels. Values are never cut; if
 * even the bare composition does not fit the row grows (heights are minima).
 */
export function planUsageHeader(width: number, tier: HeaderTier, m: UsageHeaderMeasures): UsageHeaderPlan {
  const inner = Math.max(0, width - 2 * m.gutter);

  if (tier !== 'phone') {
    const fixed = m.primary.full + m.gap + m.cost.full + m.gap + m.details;
    if (fixed <= inner) {
      const cap = tier === 'tablet' ? Math.min(1, m.secondaries.length) : m.secondaries.length;
      let used = fixed;
      let visible = 0;
      while (visible < cap && used + m.gap + m.secondaries[visible] <= inner) {
        used += m.gap + m.secondaries[visible];
        visible++;
      }
      return { layout: 'rows', fit: 'full', visibleSecondaries: visible, showWordmark: false, showDetails: true };
    }
    for (const fit of COMPACT_STEPS) {
      if (m.primary[fit] + m.gap + m.cost[fit] + m.gap + m.details <= inner) {
        return { layout: 'rows', fit, visibleSecondaries: 0, showWordmark: false, showDetails: true };
      }
    }
    return { layout: 'rows', fit: 'bare', visibleSecondaries: 0, showWordmark: false, showDetails: true };
  }

  const usage = (fit: UsageChipFit) => m.primary[fit] + m.gap + m.cost[fit] + m.gap + m.more;
  if (m.wordmark + m.gap + usage('compact') <= inner) {
    return { layout: 'single', fit: 'compact', visibleSecondaries: 0, showWordmark: true, showDetails: false };
  }
  if (m.wordmark + m.gap + usage('abbreviated') <= inner) {
    return { layout: 'single', fit: 'abbreviated', visibleSecondaries: 0, showWordmark: true, showDetails: false };
  }
  const fit = usage('abbreviated') <= inner ? 'abbreviated' : 'bare';
  return { layout: 'single', fit, visibleSecondaries: 0, showWordmark: false, showDetails: false };
}
