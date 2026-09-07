import { describe, expect, it } from 'vitest';
import type { TaskInfo, ReviewEvidenceEntry, ReviewProjectionView, ReviewAttempt } from '../../../../models/task.model';
import type { CodeReviewListEntry } from '../../../../services/task.service';
import type { SteeringInfo } from '../../../../components/steering-detail';
import {
  buildDelivery,
  buildEscalationEssence,
  buildEscalationSummaryView,
  deriveEscalationClass,
  deriveReissues,
  deriveRecommendation,
  escalationReasonClass,
  gateItemsFromCouncil,
  gateItemsFromEvidence,
  gateItemsFromFindings,
  gradeTone,
  parseStatusStubEscalation,
  pickReviewHead,
  resolveGateItems,
} from './escalation-summary.util';

function evidence(over: Partial<ReviewEvidenceEntry> = {}): ReviewEvidenceEntry {
  return {
    id: over.id ?? 'e1',
    source: over.source ?? 'code-review',
    severity: over.severity ?? 'warn',
    title: over.title ?? 'Finding',
    body: over.body ?? null,
    createdAt: over.createdAt ?? '2026-07-09T10:00:00Z',
    runIndex: over.runIndex ?? null,
    artifacts: over.artifacts ?? [],
    fileRefs: over.fileRefs ?? [],
    acknowledged: over.acknowledged ?? false,
    followupJobId: over.followupJobId ?? null,
  };
}

function review(over: Partial<CodeReviewListEntry> = {}): CodeReviewListEntry {
  return {
    fileName: over.fileName ?? 'code-review-grade-2026-07-09T19-22-02Z.md',
    verdict: over.verdict ?? 'pass',
    grade: over.grade,
    summary: over.summary ?? 'Solid first slice.',
    model: over.model ?? 'claude-opus-4-8',
    cliType: over.cliType ?? 'claude',
    runAt: over.runAt ?? '2026-07-09T19:22:02Z',
    councilReaction: over.councilReaction,
  };
}

function info(over: Partial<TaskInfo> = {}): TaskInfo {
  return { orchestratorVerdict: 'escalate', ...(over as object) } as TaskInfo;
}

function reviewAttempt(over: Partial<ReviewAttempt> = {}): ReviewAttempt {
  return {
    plane: over.plane ?? 'remote',
    attemptId: over.attemptId ?? 'review_0',
    receivedAt: over.receivedAt ?? '2026-08-31T12:18:00Z',
    outcome: over.outcome ?? 'ProductFailure',
    grade: over.grade ?? null,
    buildTestsResult: over.buildTestsResult ?? 'passed',
    buildTestsReason: over.buildTestsReason ?? 'verify-1 and verify-2 passed.',
    aspects: over.aspects ?? [],
    subjectSha: over.subjectSha ?? 'a'.repeat(40),
    reportRef: over.reportRef ?? `remote-review-grade-${over.attemptId ?? 'review_0'}.md`,
  };
}

function reviewProjection(over: Partial<ReviewProjectionView> = {}): ReviewProjectionView {
  return {
    attempts: over.attempts ?? [reviewAttempt()],
    rounds: over.rounds ?? 1,
    latestPlane: over.latestPlane ?? 'remote',
    latestOutcome: over.latestOutcome ?? 'ProductFailure',
    latestReceivedAt: over.latestReceivedAt ?? '2026-08-31T12:18:00Z',
    blockingAspects: over.blockingAspects ?? [],
    delivery: over.delivery ?? { status: 'not-attempted', reason: null },
    decisionRequired: over.decisionRequired ?? { required: false, source: null, reason: null },
  };
}

describe('gateItemsFromCouncil', () => {
  it('projects typed council actions without inspecting Markdown', () => {
    const items = gateItemsFromCouncil([
      { finding: 'Fix the regression.', action: 'FixNextRound', reason: 'Required next round.' },
      { finding: 'Accepted tradeoff.', action: 'Accept', reason: 'Operator-safe.' },
      { finding: 'Budget exhausted.', action: 'Escalate', reason: 'Needs a human.' },
    ]);
    expect(items).toEqual([
      { id: 'council-0-Fix the regression.', text: 'Fix the regression.', detail: 'Required next round.', checked: false, verdict: 'fix next round', tone: 'warn' },
      { id: 'council-1-Accepted tradeoff.', text: 'Accepted tradeoff.', detail: 'Operator-safe.', checked: true, verdict: 'accepted', tone: 'ok' },
      { id: 'council-2-Budget exhausted.', text: 'Budget exhausted.', detail: 'Needs a human.', checked: false, verdict: 'escalate', tone: 'danger' },
    ]);
  });
});

describe('gateItemsFromFindings', () => {
  it('renders aspect findings as unchecked toned items', () => {
    const items = gateItemsFromFindings([
      { aspect: 'code-quality', verdict: 'block', reason: 'helper duplicated' },
      { aspect: 'requirement-fit', verdict: 'concerns', reason: '' },
    ]);
    expect(items[0]).toMatchObject({
      text: 'code-quality: helper duplicated',
      checked: false,
      verdict: 'block',
      tone: 'danger',
    });
    // No reason -> label only, warn tone for concerns.
    expect(items[1]).toMatchObject({ text: 'requirement-fit', tone: 'warn' });
  });
});

describe('gateItemsFromEvidence', () => {
  it('drops acknowledged findings and orders high severity first', () => {
    const items = gateItemsFromEvidence([
      evidence({ id: 'a', severity: 'info', title: 'info one' }),
      evidence({ id: 'b', severity: 'high', title: 'high one' }),
      evidence({ id: 'c', severity: 'warn', title: 'ack', acknowledged: true }),
    ]);
    expect(items.map((i) => i.text)).toEqual(['high one', 'info one']);
    expect(items[0].tone).toBe('danger');
  });
});

describe('resolveGateItems priority', () => {
  const steering: SteeringInfo = {
    verdict: 'escalate',
    verdictLabel: 'Escalate',
    tone: 'danger',
    reason: 'completion gate found unfinished work',
    openItems: [{ aspect: 'tests', verdict: 'block', reason: 'missing regression' }],
    prompt: null,
    context: [{ key: 'Cause', value: 'completion-gate' }],
    commits: [],
  };

  it('prefers the newest typed council reaction over findings and evidence', () => {
    const { items, source } = resolveGateItems({
      codeReviews: [review({
        grade: 'B',
        councilReaction: {
          createdAt: '2026-07-09T19:22:03Z',
          reviewFileName: 'code-review-grade-2026-07-09T19-22-02Z.md',
          grade: 'B',
          disposition: 'Escalate',
          summary: 'Escalate one finding.',
          assessments: [{ finding: 'do the thing', action: 'Escalate', reason: 'budget exhausted' }],
          startsNewRound: false,
          targetJobId: null,
          targetRunAttempt: null,
        },
      })],
      steering,
      reviewEvidence: [evidence()],
    });
    expect(source).toBe('council-reaction');
    expect(items).toHaveLength(1);
  });

  it('falls back to gate findings when the latest review has no council sidecar', () => {
    const { source } = resolveGateItems({
      codeReviews: [review({ grade: 'B' })],
      steering,
      reviewEvidence: [evidence()],
    });
    expect(source).toBe('gate-evidence');
  });

  it('falls back to review evidence when neither follow-up nor findings exist', () => {
    const { source, items } = resolveGateItems({
      codeReviews: [],
      steering: null,
      reviewEvidence: [evidence({ title: 'lonely finding' })],
    });
    expect(source).toBe('review-evidence');
    expect(items[0].text).toBe('lonely finding');
  });

  it('reports none when there is nothing to show', () => {
    expect(
      resolveGateItems({ codeReviews: [], steering: null, reviewEvidence: [] }).source,
    ).toBe('none');
  });
});

describe('gradeTone', () => {
  it('maps A/B to ok, C to warn, D to danger, unknown to neutral', () => {
    expect(gradeTone('A')).toBe('ok');
    expect(gradeTone('b')).toBe('ok');
    expect(gradeTone('C')).toBe('warn');
    expect(gradeTone('D')).toBe('danger');
    expect(gradeTone(null)).toBe('neutral');
  });
});

describe('pickReviewHead', () => {
  it('prefers the newest graded entry over an ungraded verdict', () => {
    const head = pickReviewHead([
      review({ grade: undefined, verdict: 'concerns', runAt: '2026-07-09T20:00:00Z' }),
      review({ grade: 'B', verdict: 'pass', runAt: '2026-07-09T19:00:00Z' }),
    ]);
    expect(head?.grade).toBe('B');
    expect(head?.gradeTone).toBe('ok');
    expect(head?.verdictTone).toBe('pass');
  });

  it('falls back to the newest entry when none carry a grade', () => {
    const head = pickReviewHead([
      review({ grade: undefined, verdict: 'block', runAt: '2026-07-09T21:00:00Z' }),
      review({ grade: undefined, verdict: 'pass', runAt: '2026-07-09T18:00:00Z' }),
    ]);
    expect(head?.grade).toBeNull();
    expect(head?.verdict).toBe('block');
  });

  it('returns null for an empty list', () => {
    expect(pickReviewHead([])).toBeNull();
  });
});

describe('buildDelivery', () => {
  it('counts commits and de-duplicates changed files across them', () => {
    const d = buildDelivery(
      info({
        commits: [
          { sha: 'a', shortSha: 'a', message: 'm', filesChanged: 2, files: ['x.ts', 'y.ts'], at: '' },
          { sha: 'b', shortSha: 'b', message: 'm', filesChanged: 2, files: ['y.ts', 'z.ts'], at: '' },
        ],
        mergeSignal: {
          branch: 'task/AGT-1994',
          inIntegration: true,
          inRelease: false,
          integrationBranch: 'develop',
          releaseBranch: 'main',
          integrationSha: 'abc1234',
          releaseSha: null,
        },
      } as Partial<TaskInfo>),
    );
    expect(d.commitCount).toBe(2);
    expect(d.filesChanged).toBe(3); // x, y, z distinct
    expect(d.merge?.develop.merged).toBe(true);
    expect(d.merge?.main.merged).toBe(false);
  });

  it('sums filesChanged when no file lists are present', () => {
    const d = buildDelivery(
      info({
        commits: [
          { sha: 'a', shortSha: 'a', message: 'm', filesChanged: 3, files: [], at: '' },
        ],
      } as Partial<TaskInfo>),
    );
    expect(d.filesChanged).toBe(3);
  });
});

describe('deriveRecommendation', () => {
  it('maps escalate -> needs decision (danger)', () => {
    expect(deriveRecommendation('escalate')).toEqual({
      kind: 'needs-decision',
      label: 'Needs decision',
      tone: 'danger',
    });
  });
  it('maps accept and reissue', () => {
    expect(deriveRecommendation('accept')?.kind).toBe('accept');
    expect(deriveRecommendation('reissue')?.tone).toBe('warn');
  });
  it('returns null for pending / absent', () => {
    expect(deriveRecommendation('pending')).toBeNull();
    expect(deriveRecommendation(null)).toBeNull();
  });
});

describe('buildEscalationSummaryView', () => {
  it('assembles the full view model for the AGT-1994 shape', () => {
    const view = buildEscalationSummaryView({
      info: info({
        orchestratorVerdict: 'escalate',
        commits: [
          { sha: 'b2ed3f47', shortSha: 'b2ed3f47', message: 'm', filesChanged: 4, files: ['a', 'b', 'c', 'd'], at: '' },
        ],
        mergeSignal: {
          branch: 'task/AGT-1994',
          inIntegration: true,
          inRelease: true,
          integrationBranch: 'develop',
          releaseBranch: 'main',
          integrationSha: 'b2ed3f4',
          releaseSha: '1a526e9',
        },
        reviewProjection: reviewProjection({
          attempts: [reviewAttempt({
            plane: 'local',
            attemptId: 'code-review-grade-2026-07-09T19-22-02Z',
            receivedAt: '2026-07-09T19:22:02Z',
            outcome: 'pass',
            grade: 'B',
            buildTestsResult: 'not-proven',
            buildTestsReason: null,
            reportRef: 'code-review-grade-2026-07-09T19-22-02Z.md',
          })],
          rounds: 1,
          latestPlane: 'local',
          latestOutcome: 'pass',
          latestReceivedAt: '2026-07-09T19:22:02Z',
          delivery: { status: 'integrated', reason: null },
        }),
      } as Partial<TaskInfo>),
      reviewEvidence: [],
      codeReviews: [review({
        grade: 'B',
        verdict: 'pass',
        councilReaction: {
          createdAt: '2026-07-09T19:22:03Z',
          reviewFileName: 'code-review-grade-2026-07-09T19-22-02Z.md',
          grade: 'B',
          disposition: 'Escalate',
          summary: 'Escalate two findings.',
          assessments: [
            { finding: 'Frontend verification missing.', action: 'Escalate', reason: 'Budget exhausted.' },
            { finding: 'Live probe missing.', action: 'Escalate', reason: 'Budget exhausted.' },
          ],
          startsNewRound: false,
          targetJobId: null,
          targetRunAttempt: null,
        },
      })],
      steering: {
        verdict: 'escalate',
        verdictLabel: 'Escalate',
        tone: 'danger',
        reason: 'completion gate found unfinished work',
        openItems: [],
        prompt: null,
        context: [{ key: 'Cause', value: 'completion-gate' }],
        commits: [],
      },
      statusMarkdown: null,
      timeline: [
        {
          ts: '2026-07-09T18:30:00Z',
          kind: 'quality_loop_reopened',
          actor: 'quality-loop',
          summary: 'Reopened after build gate.',
          details: { cause: 'build/test gate failed', reason: 'npm test exited with 1' },
        },
        {
          ts: '2026-07-09T19:30:00Z',
          kind: 'orchestrator_escalated',
          actor: 'orchestrator',
          summary: 'Budget exhausted.',
          details: { attempt: '3', maxAttempts: '3' },
        },
      ],
    });

    expect(view.gateSource).toBe('council-reaction');
    expect(view.gateItems).toHaveLength(2);
    expect(view.review?.grade).toBe('B');
    expect(view.reason).toBe('completion gate found unfinished work');
    expect(view.cause).toBe('completion-gate');
    expect(view.delivery.commitCount).toBe(1);
    expect(view.delivery.filesChanged).toBe(4);
    expect(view.delivery.merge?.main.merged).toBe(true);
    expect(view.recommendation?.kind).toBe('needs-decision');
    expect(view.reissues[0].trigger).toBe('build/test gate failed: npm test exited with 1');
    expect(view.essence.label).toBe(
      '1 review round (local) · latest 09.07. 19:22 pass · build and tests not proven · in develop',
    );
    // A completion-gate escalation is a logical / quality review, not a give-up.
    expect(view.escalation?.kind).toBe('needs-review');
  });
});

describe('buildEscalationEssence', () => {
  it('falls back to "0 review rounds" plus the reason class when the task carries no review attempt at all', () => {
    const essence = buildEscalationEssence({
      reviewProjection: null,
      codeReviews: [],
      timeline: [{
        ts: '2026-07-09T20:00:00Z', kind: 'orchestrator_escalated', actor: 'orchestrator',
        summary: 'The Markdown body may be arbitrarily long.', details: { attempt: '3', maxAttempts: '3' },
      }],
      steering: null,
    });
    expect(essence).toEqual({
      reviewRounds: 0,
      latestPlane: null,
      latestOutcome: null,
      latestReceivedAt: null,
      blockingAspect: null,
      buildTestsSummary: null,
      deliverySummary: null,
      reasonClass: 'Reissue budget exhausted',
      label: '0 review rounds · Reissue budget exhausted',
    });
    expect(essence.label).not.toContain('Markdown body');
  });

  it('names seven remote rounds, the blocking aspect, the passing build, and the failed gate (AGT-2689)', () => {
    const attempts = [
      reviewAttempt({ attemptId: 'review_6', receivedAt: '2026-08-31T17:11:00Z' }),
      reviewAttempt({ attemptId: 'review_5', receivedAt: '2026-08-31T16:22:00Z' }),
      reviewAttempt({ attemptId: 'review_4', receivedAt: '2026-08-31T15:33:00Z' }),
      reviewAttempt({ attemptId: 'review_3', receivedAt: '2026-08-31T14:44:00Z' }),
      reviewAttempt({ attemptId: 'review_2', receivedAt: '2026-08-31T13:55:00Z' }),
      reviewAttempt({ attemptId: 'review_1', receivedAt: '2026-08-31T13:06:00Z' }),
      reviewAttempt({ attemptId: 'review_0', receivedAt: '2026-08-31T12:18:00Z' }),
    ];
    const essence = buildEscalationEssence({
      reviewProjection: reviewProjection({
        attempts,
        rounds: 7,
        latestReceivedAt: attempts[0].receivedAt,
        blockingAspects: [{
          aspect: 'documentation-impact',
          reason: 'Public API and state-file contract changed without corresponding load-bearing doc updates',
        }],
        delivery: { status: 'gate-failed', reason: 'Remote delivery gate failed before integration.' },
        decisionRequired: { required: true, source: 'parked-blocker', reason: 'Operator decision needed.' },
      }),
      codeReviews: [],
      timeline: [],
      steering: null,
    });
    expect(essence.reviewRounds).toBe(7);
    expect(essence.blockingAspect).toEqual({
      aspect: 'documentation-impact',
      reason: 'Public API and state-file contract changed without corresponding load-bearing doc updates',
    });
    expect(essence.label).toBe(
      '7 review rounds (remote) · latest 31.08. 17:11 ProductFailure · '
      + 'blocked by documentation-impact: Public API and state-file contract changed without corresponding load-bearing doc updates · '
      + 'build and tests pass · delivery gate failed, not in develop',
    );
    // Never "0 rounds" or "Grade not recorded" when review reports exist.
    expect(essence.label).not.toContain('0 review round');
    expect(essence.label).not.toContain('Grade not recorded');
  });

  it('uses structured cause and council disposition fallbacks', () => {
    expect(escalationReasonClass([], {
      verdict: 'escalate', verdictLabel: 'Escalate', tone: 'danger', reason: '# raw Markdown',
      openItems: [], prompt: null, context: [{ key: 'Cause', value: 'completion-gate' }], commits: [],
    }, [])).toBe('Completion gate');
  });
});

describe('deriveRecommendation with a blocking aspect', () => {
  it('names the concrete gap and both operator options', () => {
    expect(deriveRecommendation('escalate', [
      { aspect: 'documentation-impact', reason: 'Docs missing.' },
    ])).toEqual({
      kind: 'needs-decision',
      label: 'Reissue for documentation-impact, or accept with override',
      tone: 'danger',
    });
  });
});

describe('deriveReissues', () => {
  it('numbers reopen events chronologically and explains each trigger', () => {
    const rows = deriveReissues([
      {
        ts: '2026-07-09T10:00:00Z', kind: 'quality_loop_reopened', actor: 'quality-loop',
        summary: 'reopened', details: { cause: 'build/test gate failed', reason: 'npm test exit 1' },
      },
      {
        ts: '2026-07-09T11:00:00Z', kind: 'quality_loop_reopened', actor: 'quality-loop',
        summary: 'reopened', details: { reason: 'bundle budget and apply_patch stderr' },
      },
    ]);
    expect(rows.map((row) => [row.index, row.at, row.trigger])).toEqual([
      [1, '2026-07-09T10:00:00Z', 'build/test gate failed: npm test exit 1'],
      [2, '2026-07-09T11:00:00Z', 'bundle budget and apply_patch stderr'],
    ]);
  });
});

describe('parseStatusStubEscalation', () => {
  it('lifts the category + reason from a BuildStatusStub status.md', () => {
    const md = [
      '# Status',
      '',
      '- Result: Escalated to human decision (infra-crash)',
      '',
      'This card was routed to 5e-escalated by the orchestrator runtime ...',
      '',
      '- Category: infra-crash',
      '- Reason: The agent CLI crashed hard (exitCode -1) before a verdict.',
      '- See `logs/` in this folder for the run output.',
    ].join('\n');
    expect(parseStatusStubEscalation(md)).toEqual({
      category: 'infra-crash',
      reason: 'The agent CLI crashed hard (exitCode -1) before a verdict.',
    });
  });

  it('returns null when the status carries a real agent summary (no Category line)', () => {
    expect(parseStatusStubEscalation('# Status\n\n- Result: Done. Shipped the feature.')).toBeNull();
    expect(parseStatusStubEscalation(null)).toBeNull();
  });
});

describe('deriveEscalationClass', () => {
  const noSteer = { steering: null };

  it('classifies an infra-crash status stub as a GaveUpToHuman terminal', () => {
    const cls = deriveEscalationClass({
      info: info({ orchestratorVerdict: 'escalate' }),
      statusMarkdown: '# Status\n- Category: infra-crash\n- Reason: process died at exit -1.',
      ...noSteer,
    });
    expect(cls).toEqual({
      kind: 'gave-up',
      category: 'infra-crash',
      categoryLabel: 'Infra crash',
      reason: 'process died at exit -1.',
    });
  });

  it('sniffs the aspect-infra give-up out of the steering cause when no stub exists', () => {
    const cls = deriveEscalationClass({
      info: info({ orchestratorVerdict: 'escalate' }),
      statusMarkdown: null,
      steering: {
        verdict: 'escalate',
        verdictLabel: 'Escalate',
        tone: 'danger',
        reason: 'aspect runner died',
        openItems: [],
        prompt: null,
        context: [{ key: 'Cause', value: 'aspect-verdict-infra-crash' }],
        commits: [],
      },
    });
    expect(cls?.kind).toBe('gave-up');
    expect(cls?.category).toBe('infra-crash');
    expect(cls?.reason).toBe('aspect runner died');
  });

  it('treats an escalate verdict with no give-up category as a logical NeedsReview', () => {
    const cls = deriveEscalationClass({
      info: info({ orchestratorVerdict: 'escalate' }),
      statusMarkdown: '# Status\n- Result: Done. Real summary, no category line.',
      ...noSteer,
    });
    expect(cls).toEqual({ kind: 'needs-review', category: null, categoryLabel: null, reason: null });
  });

  it('returns null when the card is not an escalation and no give-up stub exists', () => {
    expect(
      deriveEscalationClass({
        info: info({ orchestratorVerdict: 'accept' }),
        statusMarkdown: null,
        ...noSteer,
      }),
    ).toBeNull();
  });
});
