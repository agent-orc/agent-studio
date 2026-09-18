import { describe, expect, it } from 'vitest';
import {
  BLOCKER_PHRASES,
  deriveProtocolVerdict,
  parseDuration,
  resolveAuthoritativeRunOutcome,
  scanForBlockers,
  stripStatusHeader,
  type ProtocolVerdictInputs,
} from './protocol-verdict';

function baseInputs(overrides: Partial<ProtocolVerdictInputs> = {}): ProtocolVerdictInputs {
  return {
    isRunning: false,
    summaryStatus: 'ready',
    statusMarkdown: null,
    outcomeIssue: null,
    hasActivity: true,
    ...overrides
  };
}

describe('deriveProtocolVerdict', () => {
  describe('authoritative precedence', () => {
    const statuses = ['failed', 'needs-decision', 'unclear', 'succeeded'] as const;

    it.each(statuses)('%s wins over every lower-precedence signal regardless of order', (winner) => {
      const lower = statuses.slice(statuses.indexOf(winner) + 1);
      for (const candidates of [lower, [...lower].reverse()]) {
        const signals = candidates.map((status) => ({
          source: 'status' as const,
          status,
          label: status,
          detail: status,
        }));
        const winningSignal = {
          source: 'runner' as const,
          status: winner,
          label: `winner-${winner}`,
          detail: winner,
        };
        for (const ordered of [[winningSignal, ...signals], [...signals, winningSignal]]) {
          expect(resolveAuthoritativeRunOutcome(ordered)?.label).toBe(`winner-${winner}`);
        }
      }
    });

    it('returns null without signals and keeps source order for equal statuses', () => {
      expect(resolveAuthoritativeRunOutcome([])).toBeNull();
      const first = { source: 'runner' as const, status: 'failed' as const, label: 'Runner', detail: 'first' };
      const second = { source: 'pipeline' as const, status: 'failed' as const, label: 'Pipeline', detail: 'second' };
      expect(resolveAuthoritativeRunOutcome([first, second])).toBe(first);
    });
  });

  describe('authoritative outcome conflict regressions (AGT-2205, AGT-2206, QS-26)', () => {
    it.each(['AGT-2205', 'AGT-2206'])('%s renders only the current running outcome, not stale terminals', () => {
      const v = deriveProtocolVerdict(baseInputs({
        isRunning: true,
        statusMarkdown: '# Status\n- Result: Success\n[[TASK_' + 'DONE]]',
        outcomeIssue: { kind: 'capture-fail', label: 'Last run error', severity: 'High', summary: 'No reply', lastSeenAt: null },
        orchestratorVerdict: 'accept',
        activityOutcome: { kind: 'failed', summary: 'Last run ended with an error.', question: null, suggestions: [] },
      }));

      expect(v.status).toBe('unclear');
      expect(v.label).toBe('Running');
      expect(v.signals).toHaveLength(1);
      expect(v.signals?.[0].label).toBe('Run is active');
    });

    it('QS-26 lets watchdog failure outrank accepted pipeline and partial result', () => {
      const v = deriveProtocolVerdict(baseInputs({
        statusMarkdown: '# Status\n- Result: Partial',
        outcomeIssue: { kind: 'watchdog-timeout', label: 'Watchdog timeout', severity: 'High', summary: 'The run will finalize as failed.', lastSeenAt: null },
        orchestratorVerdict: 'accept',
        laneState: '5-human-review',
        pipelineExecution: {
          pipelineId: 'standard', pipelineVersion: 1, jobId: 'QS-26', project: 'quality',
          startedAt: '2026-07-22T10:00:00Z', completedAt: '2026-07-22T10:02:00Z', steps: [],
        },
      }));

      expect(v.status).toBe('failed');
      expect(v.kind).toBe('problem');
      expect(v.label).toBe('Watchdog timeout');
      expect(v.signals?.map(signal => signal.status)).toEqual(expect.arrayContaining(['failed', 'needs-decision', 'succeeded']));
    });
  });

  it('is unclear (running) when isRunning beats every other signal', () => {
    const v = deriveProtocolVerdict(baseInputs({
      isRunning: true,
      statusMarkdown: '# Status\n- Result: Success\n[[TASK_' + 'DONE]]'
    }));
    expect(v.kind).toBe('unclear');
    expect(v.emoji).toBe('🟡');
    expect(v.label).toBe('Running');
  });

  it('keeps the run unclear when summary generation itself failed', () => {
    const v = deriveProtocolVerdict(baseInputs({ summaryStatus: 'failed', statusMarkdown: null }));
    expect(v.kind).toBe('unclear');
    expect(v.emoji).toBe('🟡');
    expect(v.label).toBe('Result summary failed');
  });

  it('keeps an exhausted summary gate reviewable as a typed degraded Result', () => {
    const v = deriveProtocolVerdict(baseInputs({ summaryStatus: 'degraded', statusMarkdown: null }));
    expect(v.kind).toBe('unclear');
    expect(v.label).toBe('Result degraded');
    expect(v.detail).toContain('core run remains reviewable');
  });

  it('flags problem on a high-severity outcome issue', () => {
    const v = deriveProtocolVerdict(baseInputs({
      outcomeIssue: { kind: 'watchdog-timeout', label: 'Watchdog', severity: 'High', summary: '120s silence', lastSeenAt: null }
    }));
    expect(v.kind).toBe('problem');
    expect(v.label).toBe('Watchdog');
  });

  it('reads a done sentinel as ok', () => {
    const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: 'done\n\n[[TASK_' + 'DONE]]\n' }));
    expect(v.kind).toBe('ok');
    expect(v.label).toBe('Done');
  });

  it('reads a blocked sentinel as needs-decision with the reason', () => {
    const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: '[[TASK_' + 'BLOCKED:missing API key]]' }));
    expect(v.kind).toBe('unclear');
    expect(v.status).toBe('needs-decision');
    expect(v.label).toBe('Blocked');
    expect(v.detail).toContain('missing API key');
  });

  it('demotes a blocked status document from an older attempt immediately', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '[[TASK_' + 'BLOCKED:old attempt could not reach the service]]',
      statusSuperseded: true,
      laneState: '3-progress',
    }));

    expect(v.kind).toBe('unclear');
    expect(v.label).toBe('Current attempt');
    expect(v.detail).toContain('newer attempt');
    expect(v.signals).toEqual([expect.objectContaining({
      source: 'status',
      status: 'unclear',
      label: 'Current attempt',
    })]);
    expect(v.duration).toBeNull();
  });

  it('reads a needs-input sentinel as unclear', () => {
    const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: 'q\n[[TASK_' + 'NEEDS_INPUT:which env?]]' }));
    expect(v.kind).toBe('unclear');
    expect(v.label).toBe('Needs input');
  });

  it('reads a no-op sentinel as ok', () => {
    const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: '[[TASK_' + 'NOOP]]' }));
    expect(v.kind).toBe('ok');
  });

  it('uses last sentinel when multiple are present', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '[[TASK_' + 'BLOCKED:earlier]]\n\n[[TASK_' + 'DONE]]'
    }));
    expect(v.kind).toBe('ok');
  });

  it('falls back to the Result: line when no sentinel is present', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '# Status\n\n- Result: Success\n- Duration: 4 min\n'
    }));
    expect(v.kind).toBe('ok');
    expect(v.label).toBe('Success');
  });

  it('maps Result: Failed -> problem', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '# Status\n- Result: Failed\n'
    }));
    expect(v.kind).toBe('problem');
  });

  it('maps committed-partial execution to needs-decision without status.md', () => {
    const v = deriveProtocolVerdict(baseInputs({
      execution: {
        jobId: 'AGT-partial',
        taskKey: 'AGT-partial',
        processId: 42,
        status: 'completed',
        runOutcome: 'committed-partial',
        startedAt: '2026-07-22T10:00:00Z',
        exitCode: -1,
        durationSeconds: 120,
        model: 'gpt-5.6-sol',
      },
    }));
    expect(v.status).toBe('needs-decision');
    expect(v.label).toBe('Partial result');
  });

  it('maps Result: Partial -> unclear', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '# Status\n- Result: Partial\n'
    }));
    expect(v.kind).toBe('unclear');
    expect(v.label).toBe('Partial');
  });

  it('maps Result: NeedsInput -> unclear', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '# Status\n- Result: NeedsInput\n'
    }));
    expect(v.kind).toBe('unclear');
  });

  it('uses warn-severity outcome issue when no sentinel/result is available', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: null,
      outcomeIssue: { kind: 'classifier-unknown', label: 'Unknown reply', severity: 'Warn', summary: '', lastSeenAt: null }
    }));
    expect(v.kind).toBe('unclear');
    expect(v.label).toBe('Unknown reply');
  });

  it('is unclear when there is activity but no signal lands', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '# Status\n\n## What Was Done\n- did stuff\n',
      hasActivity: true
    }));
    expect(v.kind).toBe('unclear');
    expect(v.label).toBe('Unclear');
  });

  it('is unclear (no run yet) when no signal and no activity', () => {
    const v = deriveProtocolVerdict(baseInputs({
      summaryStatus: 'none',
      statusMarkdown: null,
      hasActivity: false
    }));
    expect(v.kind).toBe('unclear');
    expect(v.label).toBe('No run yet');
  });

  it('treats sentinel as authoritative over Result line', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '# Status\n- Result: Failed\n\n[[TASK_' + 'DONE]]'
    }));
    expect(v.kind).toBe('ok');
  });

  // Regression: status.md said Result: Success but the Notes section described a
  // sandbox-denied blocker. Verdict was rendered as green Success; user wanted
  // red Problem because the task was not actually fulfilled.
  describe('Result: Success body-blocker downgrade', () => {
    it('downgrades Result: Success to Blocked when Notes contains "was blocked"', () => {
      const md = [
        '# Status',
        '',
        '- Result: Success',
        '- Duration: 4 min',
        '',
        '## What Was Done',
        '- Attempted NuGet restore',
        '',
        '## Notes',
        '- Implementation was blocked by `obj/*.tmp` access denied during NuGet restore.'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('unclear');
      expect(v.status).toBe('needs-decision');
      expect(v.label).toBe('Blocked');
      expect(v.detail).toContain('Notes');
      expect(v.detail.toLowerCase()).toContain('blocked');
    });

    it('downgrades on "access denied" in Open Items', () => {
      const md = [
        '# Status',
        '- Result: Success',
        '',
        '## Open Items',
        '- Sandbox access denied; needs external verification.'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('unclear');
      expect(v.detail).toContain('Open Items');
    });

    it('downgrades on "konnte nicht" (German) in Notes', () => {
      const md = [
        '# Status',
        '- Result: Success',
        '',
        '## Notes',
        '- Build konnte nicht abgeschlossen werden.'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('unclear');
    });

    it('downgrades on "requires external" in What Was Done', () => {
      const md = [
        '# Status',
        '- Result: Success',
        '',
        '## What Was Done',
        '- Edit applied locally; requires external verification before merging.'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('unclear');
    });

    it('keeps Result: Success when blocker phrase only appears in unrelated sections', () => {
      const md = [
        '# Status',
        '- Result: Success',
        '',
        '## Images',
        '- ![](results/blocked-icon.png)',
        '',
        '## What Was Done',
        '- All tests passed.'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('ok');
      expect(v.label).toBe('Success');
    });

    it('does not downgrade Result: NoOp or other non-success results', () => {
      const md = [
        '# Status',
        '- Result: NoOp',
        '',
        '## Notes',
        '- Was blocked earlier but recovered.'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('ok');
    });

    it('lets a done sentinel beat the body-blocker downgrade', () => {
      const md = [
        '# Status',
        '- Result: Success',
        '',
        '## Notes',
        '- Was briefly blocked but recovered.',
        '',
        '[[TASK_' + 'DONE]]'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('ok');
    });
  });

  describe('duration carried on the verdict pill', () => {
    it('parses "- Duration: 4 min" from a typical # Status header', () => {
      const md = ['# Status', '', '- Result: Success', '- Duration: 4 min', '', '## Notes', '- ok'].join('\n');
      expect(parseDuration(md)).toBe('4 min');
    });

    it('parses Duration even when nested under ## Status (legacy authoring)', () => {
      const md = ['## Status', '- Result: Failed', '- Duration: 12s'].join('\n');
      expect(parseDuration(md)).toBe('12s');
    });

    it('returns null when no Duration line is present', () => {
      expect(parseDuration('# Status\n- Result: Success')).toBeNull();
      expect(parseDuration(null)).toBeNull();
      expect(parseDuration('')).toBeNull();
    });

    it('attaches duration to the resolved verdict', () => {
      const v = deriveProtocolVerdict(baseInputs({
        statusMarkdown: '# Status\n\n- Result: Success\n- Duration: 4 min\n'
      }));
      expect(v.duration).toBe('4 min');
      expect(v.kind).toBe('ok');
    });

    it('verdict.duration is null when status.md omits Duration', () => {
      const v = deriveProtocolVerdict(baseInputs({
        statusMarkdown: '# Status\n\n- Result: Success\n'
      }));
      expect(v.duration).toBeNull();
    });

    it('verdict.duration is null when no markdown exists yet', () => {
      const v = deriveProtocolVerdict(baseInputs({ summaryStatus: 'none', statusMarkdown: null, hasActivity: false }));
      expect(v.duration).toBeNull();
    });
  });

  describe('stripStatusHeader', () => {
    it('removes the # Status section + Result/Duration list before the next heading', () => {
      const md = [
        '# Status',
        '',
        '- Result: Success',
        '- Duration: 4 min',
        '',
        '## What Was Done',
        '- did stuff',
        '',
        '## Notes',
        '- a note'
      ].join('\n');
      const out = stripStatusHeader(md);
      expect(out).not.toContain('# Status');
      expect(out).not.toContain('Duration:');
      expect(out).not.toMatch(/Result:\s*Success/);
      expect(out).toMatch(/^## What Was Done/);
      expect(out).toContain('did stuff');
      expect(out).toContain('## Notes');
    });

    it('also strips ## Status (lower heading level)', () => {
      const md = ['## Status', '- Result: Failed', '- Duration: 2 min', '', '## Notes', '- broke'].join('\n');
      const out = stripStatusHeader(md);
      expect(out).not.toContain('Status');
      expect(out).toMatch(/^## Notes/);
    });

    it('leaves status.md untouched when there is no Status heading', () => {
      const md = '## Other\n- nope';
      expect(stripStatusHeader(md)).toBe(md);
    });

    it('returns empty string for null/empty input', () => {
      expect(stripStatusHeader(null)).toBe('');
      expect(stripStatusHeader('')).toBe('');
    });

    it('does not strip a heading that merely contains the word Status', () => {
      const md = ['## Status summary (the agent\'s own report)', '- detail'].join('\n');
      const out = stripStatusHeader(md);
      expect(out).toBe(md);
    });
  });

  describe('scanForBlockers', () => {
    it.each(BLOCKER_PHRASES)('detects the configured phrase "%s" as a whole phrase', (phrase) => {
      const hit = scanForBlockers(`## Notes\n- Operation ${phrase} because the dependency is unavailable.`);
      expect(hit?.phrase).toBe(phrase);
    });

    it('returns the matched phrase, sentence, and section heading', () => {
      const md = [
        '## Notes',
        '- Implementation was blocked by sandbox.'
      ].join('\n');
      const hit = scanForBlockers(md);
      expect(hit).not.toBeNull();
      expect(hit?.phrase).toBe('blocked');
      expect(hit?.section).toBe('Notes');
      expect(hit?.sentence.toLowerCase()).toContain('blocked by sandbox');
    });

    it('ignores phrase hits in non-blocker sections', () => {
      const md = [
        '## Images',
        '- access denied note in caption'
      ].join('\n');
      expect(scanForBlockers(md)).toBeNull();
    });

    it('returns null for empty/null input', () => {
      expect(scanForBlockers(null)).toBeNull();
      expect(scanForBlockers('')).toBeNull();
      expect(scanForBlockers('# Status\n- Result: Success')).toBeNull();
    });

    it('ignores inline code, fenced code, partial words, and negated blocker language', () => {
      const harmless = [
        '## What Was Done',
        '- Added `frontend/src/app/features/task-detail/parked-blocker/`.',
        '- Added the `parked-blocker` marker.',
        '- Added the cutover-blocker table.',
        '- These are concerns, not acceptance blockers.',
        '- There are no blockers and nothing blocked.',
        '- The queue is unblocked.',
        '```sh',
        'echo "blocked on missing credentials"',
        '```',
      ].join('\n');

      expect(scanForBlockers(harmless)).toBeNull();
    });

    it.each([
      'Could not push: access denied',
      'Blocked on missing credentials',
    ])('keeps a true non-integrated blocker positive: %s', (sentence) => {
      const md = `# Status\n- Result: Success\n\n## Notes\n- ${sentence}`;
      const verdict = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));

      expect(verdict.status).toBe('needs-decision');
      expect(verdict.label).toBe('Blocked');
      expect(verdict.tooltip).toContain(`from text scan: ${sentence}`);
    });
  });

  describe('completed delivery text-scan precedence (AGT-2883)', () => {
    const realFalsePositives = [
      'Added the task-detail component in `frontend/src/app/features/task-detail/parked-blocker/`.',
      'Added the marker name `parked-blocker`.',
      'Added the cutover-blocker table.',
      'The validator now rejects blocked material.',
      'These are review concerns, not acceptance blockers.',
      'The remaining concerns are not acceptance blockers.',
    ];

    it.each(realFalsePositives)('keeps an integrated, passed card successful: %s', (sentence) => {
      const md = `# Status\n- Result: Success\n\n## What Was Done\n- ${sentence}`;
      const verdict = deriveProtocolVerdict(baseInputs({
        statusMarkdown: md,
        deliveryIntegrated: true,
        lastReviewPassed: true,
      }));

      expect(verdict.status).toBe('succeeded');
      expect(verdict.label).toBe('Success');
    });

    it('also trusts the completed lane when the latest review passed', () => {
      const verdict = deriveProtocolVerdict(baseInputs({
        statusMarkdown: '# Status\n- Result: Success\n\n## Notes\n- Blocked on missing credentials.',
        laneState: '6-completed',
        lastReviewPassed: true,
      }));

      expect(verdict.status).toBe('succeeded');
      expect(verdict.signals).toContainEqual(expect.objectContaining({
        label: 'status text mentions: blocked',
        sourceLabel: 'text scan',
      }));
    });

    it.each([
      { deliveryIntegrated: true, lastReviewPassed: false },
      { deliveryIntegrated: false, lastReviewPassed: true },
    ])('requires both settled delivery and a passed review: %o', (facts) => {
      const verdict = deriveProtocolVerdict(baseInputs({
        statusMarkdown: '# Status\n- Result: Success\n\n## Notes\n- Blocked on missing credentials.',
        ...facts,
      }));

      expect(verdict.status).toBe('needs-decision');
      expect(verdict.label).toBe('Blocked');
    });
  });

  describe('diagnostic verdict tooltip provenance', () => {
    it('names the status.md Result line source', () => {
      const verdict = deriveProtocolVerdict(baseInputs({ statusMarkdown: '# Status\n- Result: Success' }));
      expect(verdict.tooltip).toBe('from status.md Result line: status.md records Result: success.');
    });

    it('names the review outcome source when it leads', () => {
      const verdict = deriveProtocolVerdict(baseInputs({ orchestratorVerdict: 'accept', hasActivity: false }));
      expect(verdict.tooltip).toContain('from review outcome:');
    });
  });

  // BEFUND 1: the reason was cut mid-word ("…ts' rendering 5 canonical
  // states…") because the sentence scanner treated the dot in a file
  // extension / decimal as a sentence boundary, and the sentinel reason
  // regex stopped at the first special character.
  describe('robust blocker reason parsing (BEFUND 1)', () => {
    it('does not cut the reason at a file-extension dot', () => {
      const md = [
        '# Status',
        '- Result: Success',
        '',
        '## What Was Done',
        '- Rewrote `protocol-verdict.ts` rendering 5 canonical states, but could not verify the banner.',
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('unclear');
      expect(v.label).toBe('Blocked');
      expect(v.detail).toContain('Rewrote');
      expect(v.detail).toContain('could not verify the banner');
      // The old bug surfaced the tail "ts' rendering 5 canonical states…".
      expect(v.detail).not.toMatch(/:\s*ts['`]/);
    });

    it('keeps decimals and extensions intact in scanForBlockers', () => {
      const hit = scanForBlockers('## Notes\n- Upgrade to 5.1 was blocked by config.sys lock.');
      expect(hit?.sentence).toContain('Upgrade to 5.1 was blocked by config.sys lock');
    });

    it('keeps a single ] inside the TASK_BLOCKED sentinel reason', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ statusMarkdown: '[[TASK_' + 'BLOCKED:cannot parse arr[0] without schema]]' }),
      );
      expect(v.label).toBe('Blocked');
      expect(v.detail).toContain('cannot parse arr[0] without schema');
    });

    it('keeps quotes and colons in the TASK_BLOCKED sentinel reason', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ statusMarkdown: '[[TASK_' + 'BLOCKED:missing "API key": see docs]]' }),
      );
      expect(v.detail).toContain('missing "API key": see docs');
    });
  });

  describe('authoritative precedence keeps accepted decisions subordinate to worse run signals', () => {
    const blockedMd = '[[TASK_' + 'BLOCKED:sandbox denied write to /etc]]';

    it('leads with Blocked when no accepted stand is present', () => {
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: blockedMd }));
      expect(v.kind).toBe('unclear');
      expect(v.status).toBe('needs-decision');
      expect(v.label).toBe('Blocked');
    });

    it('keeps Blocked authoritative when orchestratorVerdict is accept', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ statusMarkdown: blockedMd, orchestratorVerdict: 'accept' }),
      );
      expect(v.kind).toBe('unclear');
      expect(v.status).toBe('needs-decision');
      expect(v.label).toBe('Blocked');
      expect(v.signals?.some(signal => signal.label === 'Review accepted')).toBe(true);
    });

    it('keeps Blocked authoritative when the card lives in 6-completed', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ statusMarkdown: blockedMd, laneState: '6-completed' }),
      );
      expect(v.kind).toBe('unclear');
      expect(v.status).toBe('needs-decision');
      expect(v.label).toBe('Blocked');
    });

    it('keeps Result: Failed above an accepted review', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ statusMarkdown: '# Status\n- Result: Failed\n', orchestratorVerdict: 'accept' }),
      );
      expect(v.kind).toBe('problem');
      expect(v.status).toBe('failed');
      expect(v.label).toBe('Failed');
    });

    it('does NOT demote under a reissue / escalate / pending decision', () => {
      for (const decision of ['reissue', 'escalate', 'pending'] as const) {
        const v = deriveProtocolVerdict(
          baseInputs({ statusMarkdown: blockedMd, orchestratorVerdict: decision }),
        );
        expect(v.kind, decision).toBe('unclear');
        expect(v.status, decision).toBe('needs-decision');
      }
    });

    it('keeps a summary failure unclear even when review accepted', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ summaryStatus: 'failed', statusMarkdown: null, orchestratorVerdict: 'accept' }),
      );
      expect(v.kind).toBe('unclear');
      expect(v.label).toBe('Result summary failed');
    });

    it('leaves an OK verdict untouched under an accepted stand', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ statusMarkdown: '[[TASK_' + 'DONE]]', orchestratorVerdict: 'accept' }),
      );
      expect(v.kind).toBe('ok');
      expect(v.label).toBe('Done');
    });
  });

});
