/**
 * The part of `GET /api/operator/capacity` (OP-5, docs/operations/operator-api.md
 * "Capacity") the usage detail reads. Read-only; reading it never changes routing.
 */
export type CapacityOutcome = 'fallback' | 'wait' | 'throttle';

/** A Ready card whose next pickup would not run its configured route at the current quota. */
export interface CapacityQueuedCard {
  project: string;
  cardKey: string;
  title: string | null;
  configuredCli: string | null;
  configuredModel: string | null;
  configuredThinkingLevel: string | null;
  outcome: CapacityOutcome | string;
  targetCli: string | null;
  targetModel: string | null;
  targetThinkingLevel: string | null;
  reason: string;
  /** When a wait asks again or a fallback may return to the configured route. */
  nextResetAt: string | null;
}

export interface OperatorCapacityResponse {
  /** Absent on a backend older than AGT-3584. */
  queuedCards?: CapacityQueuedCard[];
}
