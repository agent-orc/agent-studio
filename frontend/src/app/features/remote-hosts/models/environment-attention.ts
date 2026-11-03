import type { RemoteHost } from './remote-host.model';
import { hostReadiness } from './host-readiness';
import type { OperationalStallCase } from '../../orchestrator/models/orchestrator.model';
import type { RunnerInfrastructureFailure } from '../services/remote-hosts.service';
import type { ReviewQueueSnapshot } from '../services/review-queue.service';

export interface EnvironmentCause {
  id: string;
  statement: string;
  count: number;
  since: string | null;
  hostId: string | null;
  action: 'reconnect' | null;
}
export interface EnvironmentAttentionInput {
  hosts: readonly RemoteHost[];
  stalls: readonly OperationalStallCase[];
  identities: readonly { id: string; identityFileError?: string | null; identityFileModifiedAt?: string | null; identityRestoreHint?: string | null }[];
  failures: readonly RunnerInfrastructureFailure[];
  review: ReviewQueueSnapshot | null;
  now: number;
}

/** One visible cause per recognisable capability and error code. Unknown reasons stay separate. */
export function buildEnvironmentAttention(input: EnvironmentAttentionInput): EnvironmentCause[] {
  const causes = new Map<string, EnvironmentCause>();
  const add = (key: string, statement: string, since: string | null, hostId: string | null,
    action: EnvironmentCause['action'] = null): void => {
    const prior = causes.get(key);
    if (prior) {
      causes.set(key, { ...prior, count: prior.count + 1,
        since: earlier(prior.since, since), hostId: prior.hostId ?? hostId });
    } else causes.set(key, { id: key, statement, count: 1, since, hostId, action });
  };
  for (const host of input.hosts.filter(row => row.status !== 'retired')) {
    const readiness = hostReadiness(host, input.now);
    if (host.taskServerConnection?.status === 'unreachable') {
      const reason = host.taskServerConnection.lastError || 'Task Server route unavailable.';
      add(causeKey(reason, host.id, 'task-server:connectivity'), reason,
        host.taskServerConnection.failureStartedAt, host.id);
    } else if (readiness.state !== 'capability-ready') {
      const capability = host.capabilityHealth?.find(item =>
        item.reason === readiness.reason || item.detail === readiness.reason);
      const key = causeKey(readiness.reason, host.id, capability?.key);
      add(key, readiness.reason, capability?.firstFailureAt ?? host.taskServerConnection?.failureStartedAt ?? null, host.id);
    }
    if (host.runnerLink?.notificationRaisedAt && host.runnerLink.state !== 'up') {
      add(`link:${host.id}`, `${host.name}: ${host.runnerLink.lastError || 'Runner link is down.'}`,
        host.runnerLink.since, host.id, 'reconnect');
    }
  }
  for (const stall of input.stalls) {
    if (!stall.subject.startsWith('host:') && !stall.subject.startsWith('container:')) continue;
    const statement = stall.lastSummary || `Open stall on ${stall.subject}`;
    const key = causeKey(statement, stall.id);
    if (!causes.has(key)) add(key, statement, stall.sinceUtc ?? null, null);
  }
  for (const identity of input.identities) {
    add(causeKey(identity.identityFileError || 'Identity file needs repair.', identity.id, 'identity'),
      `${identity.identityFileError || 'Identity file needs repair.'} ${identity.identityRestoreHint || ''}`.trim(),
      identity.identityFileModifiedAt ?? null, identity.id);
  }
  for (const failure of input.failures) {
    add(`infrastructure:${failure.fingerprint || failure.taskKey}`,
      `${failure.host}: ${failure.lastError}`, null, null);
  }
  if (input.review?.isStagnant) add('review:stagnant',
    `Review queue has not drained for ${input.review.stagnantThresholdMinutes} minutes.`,
    input.review.stagnantSince, null);
  return [...causes.values()].sort((a, b) => b.count - a.count || a.statement.localeCompare(b.statement));
}

function causeKey(reason: string, fallback: string, capability?: string): string {
  const code = reason.match(/\b(?:HTTP\s*)?([45]\d\d)\b/i)?.[1];
  if (!capability && code && /task server|task-server/i.test(reason)) capability = 'task-server:connectivity';
  if (capability && code) return `${capability}:${code}`;
  if (capability) return `${capability}:${reason.trim().toLowerCase()}`;
  return `individual:${fallback}`;
}
function earlier(a: string | null, b: string | null): string | null {
  if (!a) return b;
  if (!b) return a;
  return Date.parse(a) <= Date.parse(b) ? a : b;
}
