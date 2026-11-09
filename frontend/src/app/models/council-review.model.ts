/**
 * Council review reaction vocabulary, in the wire format the API serializes
 * (`JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`, AGT-3578). The
 * backend enums are `CouncilFindingAction` and `CouncilReactionDisposition`
 * in `orchestration-decisions/CouncilReviewReaction.cs`.
 */
export type CouncilFindingAction = 'fixNextRound' | 'accept' | 'escalate';
export type CouncilReactionDisposition = 'accept' | 'reissue' | 'escalate';

const FINDING_ACTIONS: readonly CouncilFindingAction[] = ['fixNextRound', 'accept', 'escalate'];
const DISPOSITIONS: readonly CouncilReactionDisposition[] = ['accept', 'reissue', 'escalate'];

/**
 * Normalize a council finding action case-insensitively, so a camelCase wire
 * value and an older PascalCase sidecar both resolve. Null when unknown.
 */
export function councilFindingAction(value: string | null | undefined): CouncilFindingAction | null {
  const key = value?.trim().toLowerCase();
  return FINDING_ACTIONS.find((action) => action.toLowerCase() === key) ?? null;
}

/** Normalize a council reaction disposition case-insensitively. Null when unknown. */
export function councilReactionDisposition(
  value: string | null | undefined,
): CouncilReactionDisposition | null {
  const key = value?.trim().toLowerCase();
  return DISPOSITIONS.find((disposition) => disposition === key) ?? null;
}
