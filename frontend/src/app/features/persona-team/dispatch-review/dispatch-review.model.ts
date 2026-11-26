export const dispatchCriteria = [
  { key: 'conflicts', short: 'Conflicts', label: 'Conflicts', unit: 'per 100 integrated cards', weight: 30 },
  { key: 'tokens', short: 'Tokens', label: 'Tokens per accepted card', unit: 'tokens', weight: 25 },
  { key: 'rework', short: 'Rework', label: 'Share of rework', unit: 'ratio', weight: 10 },
  { key: 'forecast', short: 'Forecast', label: 'Forecast accuracy', unit: 'pair F1', weight: 10 },
  { key: 'parallelism', short: 'Parallel', label: 'Parallelism used', unit: 'ratio against bound', weight: 10 },
  { key: 'lead', short: 'Lead', label: 'Lead time', unit: 'median hours Ready to integrated', weight: 5 },
  { key: 'ownCost', short: 'Own cost', label: 'Own token cost', unit: 'tokens per 100 dispatched cards', weight: 5 },
  { key: 'explanation', short: 'Explanation', label: 'Explanation', unit: '70% check + 30% owner rating', weight: 5 },
] as const;
export type Criterion = typeof dispatchCriteria[number]['key'];
export type Measures = Record<Criterion, number | null>;
export interface WeightRevision { id: string; author: string; at: string; reason: string | null;
  weights: Record<Criterion, number>; gate: number }
export interface DispatchRaw { persona: string; strategy: string; mode: string; integratedCards: number;
  acceptedCards: number; cards: string[]; criteria: Measures; fileF1: number | null;
  tokensEstimated: boolean; warnings: string[] }
export interface DispatchRow { raw: DispatchRaw; scores: Measures; plainValue: number | null;
  value: number | null; gateMultiplier: number | null; status: string }
export interface DispatchLease { persona: string; fence: number; window: string | null; expiresAt: string;
  rollout: { champion: string; challenger: string | null; stopped: boolean; pinned: string | null;
    guard: { id: string; persona: string; at: string } | null } | null }
export interface DispatchReview { project: string; from: string; to: string; weights: WeightRevision;
  lease?: DispatchLease | null;
  personas: DispatchRow[]; weightRevisions: WeightRevision[]; observedPersonas: DispatchRaw[];
  competition: { champion: string; challenger: string;
    facts: { shadowIntegratedCards: number; shadowWaves: number; alternatingLiveWaves: number; guardCards: number };
    evaluation: { eligibleForAlternatingLive: boolean; championEligible: boolean; guardExceeded: boolean } }[] }
export interface DispatchConflict { id: string; card: string; kind: string; partnerCards: string[];
  conflictedFiles: string[]; at: string; personaId: string | null; personaMode: string | null;
  dispatchDecisionIds: Record<string, string | null>; backfilled: boolean }
export const defaultWeights = (): Record<Criterion, number> => Object.fromEntries(
  dispatchCriteria.map(c => [c.key, c.weight])) as Record<Criterion, number>;
export function validWeights(weights: Record<Criterion, number>, gate: number): boolean {
  return dispatchCriteria.every(c => Number.isInteger(weights[c.key]) && weights[c.key] >= 0 && weights[c.key] <= 100)
    && Object.values(weights).reduce((sum, weight) => sum + weight, 0) === 100
    && Number.isFinite(gate) && gate >= .5 && gate <= 1;
}
export function dispatchRoute(project: string): string { return `dispatch:${project}`; }
