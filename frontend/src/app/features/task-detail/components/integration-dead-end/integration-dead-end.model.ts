import type { TaskIntegrationStatus } from '../../../git';
import type { ReviewProjectionView } from '../../../../models/task.model';

export interface IntegrationDeadEnd {
  kind: string;
  title: string;
  stage: string;
  reason: string;
  evidence: string | null;
  files: string[];
  integrationTip: string | null;
  recheck: boolean;
  retry: boolean;
  rebase: boolean;
}

/** The card's one actionable reading of a failed delivery boundary. */
export function integrationDeadEnd(
  status: TaskIntegrationStatus | null | undefined,
  containmentUnknown = false,
  review: ReviewProjectionView | null | undefined = null,
): IntegrationDeadEnd | null {
  if (containmentUnknown || status?.reachUnavailable) {
    return {
      kind: 'host-unavailable', title: 'Integration reach unavailable',
      stage: 'git reach check', reason: status?.detail ?? 'The integration branch could not be checked.',
      evidence: null, files: [], integrationTip: null, recheck: true, retry: false, rebase: false,
    };
  }
  if (review?.blockingAspects?.length) {
    return {
      kind: 'review-concerns', title: 'Review concerns',
      stage: `${review.latestPlane ?? 'review'} review`,
      reason: review.blockingAspects.map(item => `${item.aspect}: ${item.reason}`).join('; '),
      evidence: null, files: [], integrationTip: null, recheck: false, retry: false, rebase: false,
    };
  }
  if (review?.delivery.status === 'gate-failed' && !status?.failure) {
    return {
      kind: 'delivery-gate-failed', title: 'Delivery gate failed', stage: 'delivery gate',
      reason: review.delivery.reason ?? 'The delivery gate failed.',
      evidence: null, files: [], integrationTip: null, recheck: false, retry: false, rebase: false,
    };
  }
  if (!status || status.status === 'integrated' || status.status === 'no-branch') return null;
  const failure = status.failure;
  const report = failure?.conflictReport;
  const code = failure?.code ?? (status.status === 'merged-locally' ? 'push-blocked' : 'delivery-pending');
  const title = ({
    'gate-environment-failure': 'Gate environment failed',
    'merge-conflict': 'Merge conflict',
    'build-gate-failed': 'Build gate failed',
    'delivery-gate-failed': 'Delivery gate failed',
    'review-concerns': 'Review concerns',
    'integration-push-blocked': 'Push blocked',
    'push-blocked': 'Push blocked',
    'delivery-pending': 'Delivery pending',
  } as Record<string, string>)[code] ?? failure?.label ?? 'Integration failed';
  const stage = [...(report?.stages ?? [])].reverse().find(item => item.outcome !== 'passed')?.stage
    ?? failure?.stage ?? (code === 'delivery-pending' ? 'integration reach' : code);
  return {
    kind: code, title, stage,
    reason: failure?.reason ?? status.detail ?? `Delivery is not integrated into ${status.integrationBranch}.`,
    evidence: failure?.evidenceExcerpt ?? null,
    files: report?.conflictedFiles ?? [],
    integrationTip: report?.integrationTipSha ?? null,
    recheck: false,
    retry: code === 'gate-environment-failure',
    rebase: !!failure?.rebaseRecoveryAvailable,
  };
}
