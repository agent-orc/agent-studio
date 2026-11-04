import { routeSegmentOf } from '../../../services/url-hash.util';
import type { OperatorTier } from './standing-grant.model';

/**
 * Wire types of the Owner's approval requests (SM-5, `/api/auth/iam/operator-approvals`):
 * a T2 request of an operator session waits here until the Owner approves or
 * rejects it after a step-up. An approval runs the exact request once, within
 * 15 minutes.
 */

export type OperatorApprovalState = 'pending' | 'approved' | 'rejected' | 'used' | 'expired' | 'cancelled';
export type InstructionSource = 'owner-chat' | 'card' | 'repository-file' | 'web-page' | 'log' | 'unstated';

export interface OperatorApproval {
  id: string;
  principalId: string;
  principalName: string | null;
  sessionId: string;
  actor: string;
  delegatedBy: string;
  operation: string;
  operationTitle: string;
  tier: OperatorTier;
  method: string;
  path: string;
  query: string;
  /** The request body as the Owner reviews it, with secret-like values masked; the digest binds the exact bytes. */
  parameters: string | null;
  parameterDigest: string;
  target: string | null;
  instructionSource: InstructionSource;
  reason: string | null;
  requestedAt: string;
  expiresAt: string;
  state: OperatorApprovalState;
  decidedBy: string | null;
  decidedAt: string | null;
  decisionNote: string | null;
  approvalExpiresAt: string | null;
  usedAt: string | null;
  resultStatus: number | null;
  cancelledAt: string | null;
  cancelReason: string | null;
}

const SOURCE_LABELS: Record<InstructionSource, string> = {
  'owner-chat': 'Owner chat',
  card: 'Card',
  'repository-file': 'Repository file',
  'web-page': 'Web page',
  log: 'Log',
  unstated: 'Not stated',
};

const STATE_LABELS: Record<OperatorApprovalState, string> = {
  pending: 'Waiting for you',
  approved: 'Approved',
  rejected: 'Rejected',
  used: 'Executed',
  expired: 'Expired',
  cancelled: 'Cancelled',
};

export function instructionSourceLabel(source: InstructionSource): string {
  return SOURCE_LABELS[source] ?? source;
}

/** Only the Owner's own chat is a trusted channel; anything else is content that can propose, never authorize. */
export function isUntrustedSource(source: InstructionSource): boolean {
  return source !== 'owner-chat';
}

export function approvalStateLabel(state: OperatorApprovalState): string {
  return STATE_LABELS[state] ?? state;
}

/** Pending requests first, oldest first so the next to expire leads; then decided ones, newest first. */
export function orderedApprovals(approvals: readonly OperatorApproval[]): { pending: OperatorApproval[]; decided: OperatorApproval[] } {
  const pending = approvals.filter(approval => approval.state === 'pending')
    .sort((left, right) => left.requestedAt.localeCompare(right.requestedAt));
  const decided = approvals.filter(approval => approval.state !== 'pending')
    .sort((left, right) => (right.decidedAt ?? right.requestedAt).localeCompare(left.decidedAt ?? left.requestedAt));
  return { pending, decided };
}

/** The request line as the operator session sent it, for example `DELETE /api/tasks/AGT-12?project=PROJ-001`. */
export function requestLine(approval: OperatorApproval): string {
  return `${approval.method} ${approval.path}${approval.query}`;
}

/** What happened after the decision, in one phrase. */
export function approvalResult(approval: OperatorApproval): string {
  if (approval.state === 'used') {
    const status = approval.resultStatus;
    return status === null ? 'Running' : status >= 200 && status < 300 ? `Ran, answered ${status}` : `Ran, refused with ${status}`;
  }
  if (approval.state === 'cancelled') return approval.cancelReason ?? 'Cancelled';
  if (approval.state === 'expired') return approval.decidedAt ? 'Not run within 15 minutes' : 'Not decided within 15 minutes';
  if (approval.state === 'rejected') return approval.decisionNote ?? 'Rejected';
  if (approval.state === 'approved') return 'Waiting for the agent to run it';
  return '';
}

/** The approval a link such as `#/workspace/settings/access?approval=<id>` points at, or `null`. */
export function approvalIdFromHash(hash: string): string | null {
  const route = routeSegmentOf(hash);
  const queryIndex = route?.indexOf('?') ?? -1;
  if (!route || queryIndex < 0) return null;
  return new URLSearchParams(route.slice(queryIndex + 1)).get('approval') || null;
}
