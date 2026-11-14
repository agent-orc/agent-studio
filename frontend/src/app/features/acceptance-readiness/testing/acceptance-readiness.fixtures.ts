import type { TaskInfo } from '../../../models/task.model';
import type { AcceptanceReadinessClass, AcceptanceReadinessItem } from '../models/acceptance-readiness.model';

/** A Human Review card for readiness specs. */
export function reviewCard(id: string, overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id,
    taskKey: `test::${id}`,
    title: `Card ${id}`,
    state: '5-human-review',
    order: 1,
    agent: 'codex',
    createdAt: '2026-11-09T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: `/tmp/watch/5-human-review/${id}`,
    lastActivity: '2026-11-09T09:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...overrides,
  };
}

/** The readiness answer for {@link reviewCard}. */
export function readinessItem(
  id: string,
  readiness: AcceptanceReadinessClass,
  overrides: Partial<AcceptanceReadinessItem> = {},
): AcceptanceReadinessItem {
  const refused = readiness !== 'ready' && readiness !== 'waiting-deploy' && readiness !== 'owner-decision';
  return {
    jobId: id,
    taskKey: `test::${id}`,
    key: null,
    projectName: 'Test',
    title: `Card ${id}`,
    readiness,
    headline: readiness === 'ready' ? 'Ready to accept' : 'Review concerns',
    reason: readiness === 'ready' ? 'Every completion check passes.' : 'The requirement-fit review returned concerns: The lane hides the reason.',
    stage: readiness === 'ready' ? 'admitted' : 'content',
    refusal: readiness === 'ready' ? 'Content review passed for the current brief.' : 'Completion refused: requirement-fit verdict was concerns.',
    aspect: 'requirement-fit',
    contentStatus: readiness === 'ready' ? 'pass' : 'concerns',
    concern: readiness === 'ready' ? null : 'The lane hides the reason.',
    missing: null,
    latestResultRef: 'refs/heads/runner/r1/AGT-1',
    round: refused ? { prompt: 'Close the acceptance gap on this card.', baseRef: 'refs/heads/runner/r1/AGT-1' } : null,
    ...overrides,
  };
}
