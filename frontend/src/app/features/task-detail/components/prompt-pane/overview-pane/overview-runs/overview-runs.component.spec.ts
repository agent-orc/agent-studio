import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { RunRecord } from '../../../../../run-timeline';
import { formatCompactDateTime } from '../../../../../../services/format.util';
import { OverviewRunsComponent } from './overview-runs.component';

describe('OverviewRunsComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [OverviewRunsComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });

  it('renders every card-scoped run with trigger, result, duration and a visible-row sum', () => {
    const fixture = setup([
      run(1, { intent: 'start', trigger: 'initial', status: 'completed', durationSeconds: 12 }),
      run(2, {
        intent: 'continue',
        status: 'failed',
        durationSeconds: 65,
        trigger: 'operator-continue',
        triggeredBy: 'operator local-default',
        userFollowup: 'Address the failed browser check.',
      }),
      run(3, {
        intent: 'recovery',
        trigger: 'recovery-after-crash',
        triggerReason: 'Runner process crashed.',
        status: 'running',
        durationSeconds: null,
      }),
    ]);
    const rows = all(fixture, 'overview-run-row');

    expect(rows).toHaveLength(3);
    expect(one(fixture, 'overview-runs-count').textContent?.trim()).toBe('3 runs');
    expect(rows[0].getAttribute('data-run-index')).toBe('3');
    expect(testText(rows[0], 'overview-run-trigger')).toBe('Recovery after crash: Runner process crashed.');
    expect(testText(rows[0], 'overview-run-result')).toContain('Running');
    expect(testText(rows[0], 'overview-run-duration')).toBe('In progress');
    expect(testText(rows[1], 'overview-run-trigger')).toBe('Operator continue by local-default');
    expect(testText(rows[1], 'overview-run-result')).toContain('Failed');
    expect(testText(rows[1], 'overview-run-duration')).toBe('1m 5s');
    expect(testText(rows[2], 'overview-run-trigger')).toBe('Initial start');
  });

  it('labels a legacy run without trigger fields as not recorded', () => {
    const fixture = setup([run(2, { intent: 'start', trigger: null })]);
    expect(one(fixture, 'overview-run-trigger').textContent?.trim()).toBe('Not recorded');
  });

  it('names and links the aspect that caused a review concern run', () => {
    const fixture = setup([run(2, {
      trigger: 'review-concern',
      triggerSource: 'review=review_01bb31c8;aspects=code-quality;prompt=Fix the dead assertion.',
    })]);
    fixture.componentRef.setInput('job', { id: 'AGT-2794', watchPath: '/workspace' });
    fixture.detectChanges();

    const link = one(fixture, 'overview-run-trigger') as HTMLAnchorElement;
    expect(link.textContent?.trim()).toBe('Review concern: Code Quality (review_01bb31c8)');
    expect(link.getAttribute('href')).toContain('remote-review-grade-review_01bb31c8.md');
  });

  it('shows operator provenance and links an integration failure to its pipeline report', () => {
    const fixture = setup([
      run(2, {
        trigger: 'operator-continue',
        triggeredBy: 'operator local-default',
        triggerReason: 'Fix the multi-dependency classification.',
      }),
      run(3, {
        trigger: 'integration-recovery',
        triggerReason: 'Merge conflict in README.md.',
        triggerSource: 'failure=merge-conflict',
      }),
    ]);
    fixture.componentRef.setInput('job', { id: 'AGT-2794', watchPath: '/workspace' });
    fixture.detectChanges();

    const rows = all(fixture, 'overview-run-row');
    const failureLink = rows[0].querySelector<HTMLAnchorElement>('[data-testid="overview-run-trigger"]');
    expect(failureLink?.textContent?.trim()).toBe('Integration recovery: Merge conflict in README.md.');
    expect(failureLink?.getAttribute('href')).toContain('pipeline-execution.json');
    expect(testText(rows[1], 'overview-run-trigger'))
      .toBe('Operator continue by local-default: Fix the multi-dependency classification.');
  });

  it('shows optional per-run pipeline token usage only where it was recorded', () => {
    const fixture = setup([
      run(1),
      run(2, {
        tokenSummary: {
          calls: 2,
          inputTokens: 900,
          outputTokens: 400,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
          totalTokens: 1_300,
          lastModel: 'gpt-5.6',
          lastUpdate: '2026-07-26T10:01:00Z',
          entries: [],
        },
      }),
    ]);
    const rows = all(fixture, 'overview-run-row');

    expect(all(fixture, 'overview-run-tokens')).toHaveLength(1);
    expect(testText(rows[0], 'overview-run-tokens')).toBe('1k tokens');
    expect(rows[1].querySelector('[data-testid="overview-run-tokens"]')).toBeNull();
  });

  it('stamps every row with its own absolute start time', () => {
    const fixture = setup([run(1), run(2, { startedAt: '2026-07-26T11:30:00Z' })]);
    const rows = all(fixture, 'overview-run-row');

    expect(testText(rows[0], 'overview-run-started')).toBe(
      formatCompactDateTime('2026-07-26T11:30:00Z'),
    );
    expect(
      rows[0].querySelector('[data-testid="overview-run-started"]')?.getAttribute('datetime'),
    ).toBe('2026-07-26T11:30:00Z');
    expect(testText(rows[1], 'overview-run-started')).toBe(
      formatCompactDateTime('2026-07-26T10:01:00Z'),
    );
  });

  it('drops the start stamp instead of rendering an unparseable date', () => {
    const fixture = setup([run(1, { startedAt: 'not-a-date' })]);

    expect(query(fixture, 'overview-run-started')).toBeNull();
  });

  it('names the baseline engine once and labels a run-specific model override', () => {
    const fixture = setup([
      run(1, { cli: 'codex', executionContext: null }),
      run(2, {
        cli: 'claude',
        executionContext: {
          cli: 'claude',
          model: 'claude-opus-4-1',
          permissionMode: null,
          cwd: null,
          capturedAt: '2026-07-26T10:02:00Z',
          source: 'init-frame',
          sources: [],
        },
      }),
      run(3, {
        cli: 'codex',
        executionContext: null,
        tokenSummary: {
          calls: 1,
          inputTokens: 10,
          outputTokens: 10,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
          totalTokens: 20,
          lastModel: 'gpt-5.6',
          lastUpdate: '2026-07-26T10:03:00Z',
          entries: [],
        },
      }),
    ]);
    expect(one(fixture, 'overview-runs-agent').textContent?.trim()).toBe('Codex');
    const overrides = all(fixture, 'overview-run-engine');
    expect(overrides).toHaveLength(2);
    expect(overrides.map((entry) => entry.textContent?.trim())).toEqual([
      'Codex · gpt-5.6',
      'Claude Code · opus 4.1',
    ]);
  });

  it('shows an identical agent once at panel level and keeps run rows compact', () => {
    const fixture = setup([
      run(1, { cli: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'xhigh' }),
      run(2, { cli: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'xhigh' }),
      run(3, { cli: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'xhigh' }),
    ]);

    expect(one(fixture, 'overview-runs-agent').textContent?.trim()).toBe(
      'Codex · gpt-5.6-sol · xhigh',
    );
    expect(all(fixture, 'overview-run-engine')).toHaveLength(0);
    expect(all(fixture, 'overview-run-row')[2].textContent).toContain('Run #1');
  });

  it('shows only an agent deviation on its affected run', () => {
    const fixture = setup([
      run(1, { cli: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'xhigh' }),
      run(2, { cli: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'xhigh' }),
      run(3, { cli: 'claude', model: 'claude-opus-4-1', thinkingLevel: 'high' }),
    ]);

    expect(one(fixture, 'overview-runs-agent').textContent?.trim()).toBe(
      'Codex · gpt-5.6-sol · xhigh',
    );
    const deviations = all(fixture, 'overview-run-engine');
    expect(deviations).toHaveLength(1);
    expect(deviations[0].textContent?.trim()).toBe('Claude Code · opus 4.1 · high');
  });

  it('omits the agent fact when the run recorded neither CLI nor model', () => {
    const fixture = setup([run(1, { cli: null })]);

    expect(query(fixture, 'overview-run-engine')).toBeNull();
  });

  it('surfaces the reason for runs that did not complete cleanly', () => {
    const fixture = setup([
      run(1, { status: 'completed', reason: 'resumed after restart' }),
      run(2, { status: 'failed', reason: 'Escalated: review subject could not be materialised' }),
    ]);
    const rows = all(fixture, 'overview-run-row');

    expect(testText(rows[0], 'overview-run-reason')).toContain(
      'review subject could not be materialised',
    );
    expect(rows[1].querySelector('[data-testid="overview-run-reason"]')).toBeNull();
  });

  it('uses the persisted CORE duration only when timeline rows have no duration', () => {
    const fixture = setup([
      run(1, {
        status: 'unknown',
        result: null,
        closeoutSource: 'legacy-missing',
        durationSeconds: null,
      }),
    ], 125);

    expect(one(fixture, 'overview-runs-duration').textContent?.trim()).toBe('2m 5s total');
    expect(testText(all(fixture, 'overview-run-row')[0], 'overview-run-result')).toContain(
      'Not recorded (legacy run)',
    );
    expect(testText(all(fixture, 'overview-run-row')[0], 'overview-run-duration')).toBe(
      'Not recorded (legacy run)',
    );
  });

  it('shows the derived terminal result and duration for a legacy remote run', () => {
    const fixture = setup([
      run(1, {
        status: 'completed',
        result: 'done',
        closeoutSource: 'timeline',
        durationSeconds: 1_679,
      }),
    ]);
    const row = all(fixture, 'overview-run-row')[0];

    expect(testText(row, 'overview-run-result')).toContain('Done');
    expect(testText(row, 'overview-run-duration')).toBe('27m 59s');
  });

  it('labels a terminal duration gap as unknown instead of implying a recorded duration', () => {
    const fixture = setup([
      run(1, {
        status: 'completed',
        result: 'done',
        closeoutSource: null,
        durationSeconds: null,
      }),
    ]);
    const row = all(fixture, 'overview-run-row')[0];

    expect(testText(row, 'overview-run-result')).toContain('Done');
    expect(testText(row, 'overview-run-duration')).toBe('Unknown (not recorded)');
  });

  it('renders the fallback total without inventing a run row or count badge', () => {
    const fixture = setup([], 75);

    expect(all(fixture, 'overview-run-row')).toHaveLength(0);
    expect(query(fixture, 'overview-runs-count')).toBeNull();
    expect(one(fixture, 'overview-runs-duration').textContent?.trim()).toBe('1m 15s total');
  });

  it('renders nothing before the current card has any run evidence', () => {
    const fixture = setup([]);

    expect(query(fixture, 'overview-runs')).toBeNull();
  });

  it('tracks a re-polled row by its durable run index', () => {
    const fixture = setup([run(1), run(2)]);
    const retained = all(fixture, 'overview-run-row')[0];

    fixture.componentRef.setInput('runs', [
      run(1),
      run(2, { status: 'failed' }),
      run(3, { status: 'running', durationSeconds: null }),
    ]);
    fixture.detectChanges();

    const rows = all(fixture, 'overview-run-row');
    expect(rows).toHaveLength(3);
    expect(rows[1]).toBe(retained);
    expect(testText(rows[1], 'overview-run-result')).toContain('Failed');
  });
});

function setup(runs: RunRecord[], fallbackDurationSeconds = 0) {
  const fixture = TestBed.createComponent(OverviewRunsComponent);
  fixture.componentRef.setInput('runs', runs);
  fixture.componentRef.setInput('fallbackDurationSeconds', fallbackDurationSeconds);
  fixture.detectChanges();
  return fixture;
}

function run(index: number, overrides: Partial<RunRecord> = {}): RunRecord {
  return {
    index,
    intent: 'continue',
    startedAt: `2026-07-26T10:0${index}:00Z`,
    endedAt: `2026-07-26T10:0${index}:10Z`,
    status: 'completed',
    cli: 'codex',
    exitCode: 0,
    durationSeconds: 10,
    inputSessionId: null,
    capturedSessionId: null,
    resumed: index > 1,
    reason: null,
    userFollowup: null,
    lineStart: index,
    lineEnd: index + 1,
    headShaBefore: null,
    headShaAfter: null,
    contextRef: null,
    ...overrides,
  };
}

function query(fixture: { nativeElement: HTMLElement }, testId: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
}

function one(fixture: { nativeElement: HTMLElement }, testId: string): HTMLElement {
  return query(fixture, testId) as HTMLElement;
}

function all(fixture: { nativeElement: HTMLElement }, testId: string): HTMLElement[] {
  return Array.from(
    fixture.nativeElement.querySelectorAll<HTMLElement>(`[data-testid="${testId}"]`),
  );
}

function testText(root: HTMLElement, testId: string): string {
  return root.querySelector<HTMLElement>(`[data-testid="${testId}"]`)?.textContent?.trim() ?? '';
}
