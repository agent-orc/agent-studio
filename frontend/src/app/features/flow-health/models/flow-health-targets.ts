/**
 * The one place that names the flow-health targets. The page prints each
 * target beside its figure and judges "met" against it; the derivations read
 * nothing else. Sources: the pipeline delivery-target Dossier
 * (docs/operations/pipeline-delivery-target/, section "Measurable targets")
 * for gate minutes and review time per card, and the batch gate's default
 * size range (2 to 8 cards) for grouping. The remaining values are operator
 * working targets; change them here and the page follows.
 */

/** `max`: the figure meets the target at or below it; `min`: at or above it. */
export type FlowTargetDirection = 'max' | 'min';

export type FlowFigureUnit = 'perDay' | 'cards' | 'share' | 'minutes' | 'ratio' | 'tokens' | 'usd' | 'rounds';

export interface FlowTarget {
  readonly direction: FlowTargetDirection;
  readonly value: number;
  readonly unit: FlowFigureUnit;
}

export const FLOW_FIGURE_TARGETS = {
  // Grouping
  batchesPerDay: { direction: 'min', value: 1, unit: 'perDay' },
  meanBatchSize: { direction: 'min', value: 2, unit: 'cards' },
  soloShare: { direction: 'max', value: 0.3, unit: 'share' },
  // Gate
  fullSuiteRunsPerCard: { direction: 'max', value: 0.5, unit: 'ratio' },
  gateMinutesPerCard: { direction: 'max', value: 10, unit: 'minutes' },
  gateSlotsBusy: { direction: 'max', value: 0.8, unit: 'share' },
  reuseRate: { direction: 'min', value: 0.3, unit: 'share' },
  // Review
  reviewRoundsPerCard: { direction: 'max', value: 1.5, unit: 'rounds' },
  reviewMinutesPerCard: { direction: 'max', value: 20, unit: 'minutes' },
  reviewMinutesPerAspect: { direction: 'max', value: 5, unit: 'minutes' },
  tokensPerCard: { direction: 'max', value: 2_000_000, unit: 'tokens' },
  costPerCard: { direction: 'max', value: 5, unit: 'usd' },
  backwardShare: { direction: 'max', value: 0.2, unit: 'share' },
} as const satisfies Record<string, FlowTarget>;

export type FlowFigureKey = keyof typeof FLOW_FIGURE_TARGETS;

/** A waiting stage of the "Now" block; `decision` is the operator's Human review. */
export type FlowStageKey = 'pickup' | 'review' | 'gate' | 'decision';

/** Oldest acceptable wait per stage, in minutes. */
export const FLOW_STAGE_WAIT_TARGET_MINUTES: Readonly<Record<FlowStageKey, number>> = {
  pickup: 120,
  review: 60,
  gate: 60,
  decision: 24 * 60,
};

/** A 7-day figure that differs from its 30-day value by less than this share reads as steady. */
export const FLOW_TREND_STEADY_SHARE = 0.1;
