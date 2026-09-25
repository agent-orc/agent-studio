import { describe, expect, it } from 'vitest';
import type { TaskIntegrationStatus } from '../../../git';
import type { ReviewProjectionView } from '../../../../models/task.model';
import { integrationDeadEnd } from './integration-dead-end.model';

function status(code: string, detail = 'The gate reported a failure.'): TaskIntegrationStatus {
  return {
    status: 'conflict-skipped', deliveryRef: 'task/AGT-2909', sha: null,
    integrationBranch: 'develop', detail,
    failure: {
      code, label: 'Integration failed', reason: detail,
      rebaseRecoveryAvailable: code === 'merge-conflict',
      conflictReport: code === 'merge-conflict' ? {
        integrationBranch: 'develop', integrationTipSha: 'tip-123', deliverySha: 'delivery-123',
        stages: [{ stage: 'rebase-fallback', outcome: 'conflict', conflictedFileCount: 2 }],
        conflictedFileCount: 2, conflictedFiles: ['src/a.ts', 'src/b.ts'],
        conflictedFilesTruncated: false,
      } : null,
    },
  };
}

describe('integrationDeadEnd', () => {
  it('keeps the failed conflict stage, files, and both integration sides', () => {
    const panel = integrationDeadEnd(status('merge-conflict'));
    expect(panel).toMatchObject({ stage: 'rebase-fallback', files: ['src/a.ts', 'src/b.ts'],
      integrationTip: 'tip-123', rebase: true });
  });

  it('shows the failed gate stage and test output excerpt', () => {
    const gate = status('build-gate-failed');
    const panel = integrationDeadEnd({ ...gate, failure: {
      ...gate.failure!, stage: 'pre-develop/build-test',
      evidenceExcerpt: 'Foo.Bar failed: expected 2, got 1',
    } });
    expect(panel).toMatchObject({
      stage: 'pre-develop/build-test', evidence: 'Foo.Bar failed: expected 2, got 1',
    });
  });

  it.each([
    ['build-gate-failed', 'Build gate failed'],
    ['gate-environment-failure', 'Gate environment failed'],
    ['delivery-gate-failed', 'Delivery gate failed'],
    ['review-concerns', 'Review concerns'],
    ['push-blocked', 'Push blocked'],
    ['integration-push-blocked', 'Push blocked'],
  ])('renders %s as %s with the recorded reason', (code, title) => {
    expect(integrationDeadEnd(status(code))).toMatchObject({ kind: code, title,
      reason: 'The gate reported a failure.' });
  });

  it('offers continuation for a pending delivery', () => {
    expect(integrationDeadEnd({ ...status('merge-conflict'), status: 'pending', failure: null }))
      .toMatchObject({ kind: 'delivery-pending', stage: 'integration reach', recheck: false });
  });

  it('offers a recheck when git reach is unavailable', () => {
    expect(integrationDeadEnd(status('merge-conflict'), true))
      .toMatchObject({ kind: 'host-unavailable', recheck: true, rebase: false });
  });

  it('uses the reach fault even when the old containment answer says pending', () => {
    expect(integrationDeadEnd({ ...status('merge-conflict'), status: 'pending', reachUnavailable: true }))
      .toMatchObject({ kind: 'host-unavailable', recheck: true });
  });

  it('shows every blocking finding from the latest review', () => {
    const review = {
      latestPlane: 'remote',
      blockingAspects: [
        { aspect: 'correctness', reason: 'Test Foo.Bar fails' },
        { aspect: 'safety', reason: 'Missing guard in src/api.cs' },
      ],
    } as ReviewProjectionView;
    expect(integrationDeadEnd(null, false, review)).toMatchObject({
      kind: 'review-concerns', stage: 'remote review',
      reason: 'correctness: Test Foo.Bar fails; safety: Missing guard in src/api.cs',
    });
  });

  it('does not show a dead end for an integrated delivery', () => {
    expect(integrationDeadEnd({ ...status('merge-conflict'), status: 'integrated' })).toBeNull();
  });
});
