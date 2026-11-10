import type { TaskInfo } from '../../../models/task.model';
import {
  acceptanceRefusalSource,
  type AcceptanceRefusal,
  type ServerAcceptanceRefusal,
} from './acceptance-refusal.model';

/**
 * AGT-3704: the owner-facing wording of an acceptance refusal. Only the
 * deferred toolbar component imports this file, so the sentences stay out of
 * the initial bundle (see docs/system/domains/frontend.md, initial bundle
 * ratchet).
 */
const OVERRIDE_LABEL = 'Accept with override…';

/**
 * The refusal for `info` with its reason and one action, or null when Accept
 * may act (see `acceptanceRefusalSource`).
 */
export function acceptanceRefusalFor(
  info: TaskInfo,
  server: ServerAcceptanceRefusal | null,
  awaitingGit: boolean,
): AcceptanceRefusal | null {
  const source = acceptanceRefusalSource(info, server, awaitingGit);
  if (!source) return null;
  if (source === 'server' && server)
    return { reason: server.message, action: 'override', actionLabel: OVERRIDE_LABEL, source };
  const integration = info.integration;
  const status = integration?.status;
  const branch = integration?.integrationBranch || 'develop';
  if (status === 'conflict-skipped') {
    const failure = integration?.failure;
    return {
      reason: failure?.reason || failure?.label || `Integration into ${branch} failed.`,
      action: 'retry-integration',
      actionLabel: 'Retry integration',
      source: 'hold',
    };
  }
  const reason = !integration
    ? 'No integration verdict for this delivery yet.'
    : integration.reachUnavailable
      ? `The ${branch} reach check could not run.`
      : status === 'merged-locally'
        ? `The delivery is merged locally but not yet pushed to ${branch}.`
        : status === 'partial'
          ? `Part of the delivery is not in ${branch} yet.`
          : status === 'no-branch'
            ? 'No delivery branch or commit was found for this card.'
            : `The delivery is not in ${branch} yet.`;
  return { reason, action: 'override', actionLabel: OVERRIDE_LABEL, source: 'hold' };
}
