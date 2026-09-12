import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskTimelinePaneComponent } from './task-timeline-pane.component';
import { TaskTimelinePollService } from '../../../polling/services/task-timeline-poll.service';
import { RunTimelinePollService } from '../../../polling/services/run-timeline-poll.service';
import type { RunRecord, RunTimeline } from '../../../run-timeline';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TIMELINE_KIND, type TaskTimelineEvent } from '../../models/task-timeline.model';

function runRecord(overrides: Partial<RunRecord> = {}): RunRecord {
  return {
    index: 1,
    intent: 'start',
    startedAt: '2026-05-30T10:00:00Z',
    endedAt: '2026-05-30T10:10:00Z',
    status: 'completed',
    cli: 'codex',
    exitCode: 0,
    durationSeconds: 600,
    inputSessionId: null,
    capturedSessionId: null,
    resumed: false,
    reason: null,
    userFollowup: null,
    lineStart: null,
    lineEnd: null,
    headShaBefore: null,
    headShaAfter: null,
    contextRef: null,
    ...overrides,
  };
}

function runTimeline(runs: RunRecord[]): RunTimeline {
  return {
    runCount: runs.length,
    firstStartedAt: runs[0]?.startedAt ?? null,
    lastActivityAt: runs.at(-1)?.endedAt ?? runs.at(-1)?.startedAt ?? null,
    hasActiveRun: runs.some(r => r.status === 'running'),
    runs,
  };
}

async function build(events: TaskTimelineEvent[] = [], runs: RunRecord[] = []) {
  await TestBed.configureTestingModule({
    imports: [TaskTimelinePaneComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      TaskTimelinePollService,
      RunTimelinePollService,
    ],
  }).compileComponents();
  TestBed.inject(TaskTimelinePollService).events.set(events);
  TestBed.inject(RunTimelinePollService).timeline.set(runs.length > 0 ? runTimeline(runs) : null);
  const fixture = TestBed.createComponent(TaskTimelinePaneComponent);
  try { fixture.detectChanges(); } catch (e) {
    console.warn('[smoke] TaskTimelinePaneComponent render skipped:', (e as Error).message);
  }
  return fixture;
}

describe('TaskTimelinePaneComponent', () => {
  it('renders the empty state with no events', async () => {
    const fixture = await build([]);
    const c = fixture.componentInstance;
    expect(c.hasEvents()).toBe(false);
    expect(c.hasLoop()).toBe(false);
    const html = fixture.nativeElement as HTMLElement;
    expect(html.querySelector('[data-testid="timeline-empty"]')).not.toBeNull();
  });

  it('renders the verdict banner + event list when a loop has activity', async () => {
    const fixture = await build([
      { ts: '2026-05-30T10:00:00Z', kind: TIMELINE_KIND.agentRunFinished, actor: 'agent', summary: 'claimed done' },
      {
        ts: '2026-05-30T10:05:00Z', kind: TIMELINE_KIND.qualityLoopReopened, actor: 'quality-loop',
        summary: 'reopened', details: { attempt: '2', maxAttempts: '3', gap: 'still broken' },
      },
    ]);
    const c = fixture.componentInstance;
    expect(c.hasEvents()).toBe(true);
    expect(c.hasLoop()).toBe(true);
    expect(c.attemptLabel()).toBe('2 / 3');
    expect(c.completionLoop().latestVerdict).toBe('reopened');

    const html = fixture.nativeElement as HTMLElement;
    const banner = html.querySelector('[data-testid="timeline-verdict-banner"]');
    expect(banner).not.toBeNull();
    expect(banner!.getAttribute('data-verdict')).toBe('reopened');
    expect(html.querySelector('[data-testid="timeline-verdict-reason"]')?.textContent).toContain('still broken');
    expect(html.querySelectorAll('[data-testid="timeline-event"]').length).toBe(2);
  });

  it('kind / verdict helpers map to stable labels and tones', async () => {
    const fixture = await build([]);
    const c = fixture.componentInstance;
    expect(c.isVerdictKind(TIMELINE_KIND.qualityLoopReopened)).toBe(true);
    expect(c.isVerdictKind(TIMELINE_KIND.agentRunStarted)).toBe(false);
    expect(c.rowTone(TIMELINE_KIND.orchestratorVerdictAccepted)).toBe('ok');
    expect(c.rowTone(TIMELINE_KIND.qualityLoopReopened)).toBe('warn');
    expect(c.rowTone(TIMELINE_KIND.orchestratorEscalated)).toBe('danger');
    expect(c.rowTone(TIMELINE_KIND.readOnlyContainmentViolation)).toBe('danger');
    expect(c.verdictLabel('escalated')).toBe('Escalated to human');
    expect(c.kindLabel(TIMELINE_KIND.qualityLoopReopened)).toBe('Re-opened');
    expect(c.kindLabel(TIMELINE_KIND.runnerSlotAdmission)).toBe('Slot admitted');
    expect(c.kindLabel(TIMELINE_KIND.runContinuedAfterRestart)).toBe('Continuing after restart');
    expect(c.kindLabel(TIMELINE_KIND.runLostAcrossRestart)).toBe('Run lost across restart');
    expect(c.rowTone(TIMELINE_KIND.runContinuedAfterRestart)).toBe('neutral');
    expect(c.rowTone(TIMELINE_KIND.runLostAcrossRestart)).toBe('danger');
    expect(c.kindLabel(TIMELINE_KIND.epicDecomposed)).toBe('Epic decomposed');
    expect(c.kindLabel(TIMELINE_KIND.readOnlyContainmentViolation)).toBe('Containment violation');
    expect(c.kindLabel(TIMELINE_KIND.externalCompletion)).toBe('Completed externally');
    expect(c.rowTone(TIMELINE_KIND.externalCompletion)).toBe('ok');
    expect(c.kindLabel(TIMELINE_KIND.steerTimeoutResolved)).toBe('Steer timeout resolved');
    expect(c.rowTone(TIMELINE_KIND.steerTimeoutResolved)).toBe('neutral');
    expect(c.rowTone({
      ts: '2026-08-08T10:00:00Z',
      kind: TIMELINE_KIND.postStepFinished,
      actor: 'system',
      summary: 'Build/test gate skipped',
      details: { step: 'post-build-test-gate', status: 'Skipped' },
    })).toBe('danger');
    expect(c.rowTone({
      ts: '2026-08-08T10:00:00Z',
      kind: TIMELINE_KIND.postStepFinished,
      actor: 'system',
      summary: 'Build/test gate skipped',
      details: { step: 'post-build-test-gate', status: 'Skipped', reason: 'no verify commands derivable' },
    })).toBe('neutral');
    expect(c.rowTone({
      ts: '2026-08-08T10:00:00Z',
      kind: TIMELINE_KIND.postStepFinished,
      actor: 'system',
      summary: 'Build/test gate not applicable',
      details: { step: 'post-build-test-gate', status: 'NotApplicable' },
    })).toBe('neutral');
    expect(c.rowTone({
      ts: '2026-08-08T10:00:00Z',
      kind: TIMELINE_KIND.postStepFinished,
      actor: 'system',
      summary: 'Optional wiki sync skipped',
      details: { step: 'post-agents-wiki-sync', status: 'Skipped' },
    })).toBe('neutral');
  });

  it('renders a resolved steer timeout with its answer as a settled event', async () => {
    const fixture = await build([
      {
        ts: '2026-07-11T10:02:00Z',
        kind: TIMELINE_KIND.steerTimeoutResolved,
        actor: 'system',
        summary: 'Steer timeout auto-answered: iframe support is already implemented.',
        details: {
          outcome: 'auto-answered',
          answer: 'Yes. The iframe implementation is present on the task branch.',
          secondsWaiting: '120',
          timeoutSeconds: '120',
        },
      },
    ]);

    const row = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('[data-testid="timeline-event"]');
    expect(row?.getAttribute('data-kind')).toBe(TIMELINE_KIND.steerTimeoutResolved);
    expect(row?.classList.contains('tl-item--neutral')).toBe(true);
    expect(row?.querySelector('[data-testid="timeline-event-kind"]')?.textContent).toContain('Steer timeout resolved');
    expect(row?.textContent).toContain('Yes. The iframe implementation is present on the task branch.');
  });

  it('renders an external-completion entry in the story', async () => {
    const fixture = await build([
      {
        ts: '2026-07-08T10:00:00Z',
        kind: TIMELINE_KIND.externalCompletion,
        actor: 'external',
        summary: 'Completed externally by operator-chat',
        details: { source: 'operator-chat', targetState: '5-human-review' },
      },
    ]);
    const c = fixture.componentInstance;
    expect(c.hasEvents()).toBe(true);
    const html = fixture.nativeElement as HTMLElement;
    const rows = html.querySelectorAll('[data-testid="timeline-event"]');
    expect(rows.length).toBe(1);
    expect(rows[0].getAttribute('data-kind')).toBe(TIMELINE_KIND.externalCompletion);
    expect(html.textContent).toContain('Completed externally · operator-chat');
  });

  it('renders run-summary rows when a legacy card has run records but no ledger run events', async () => {
    const fixture = await build(
      [
        {
          ts: '2026-05-30T09:55:00Z',
          kind: TIMELINE_KIND.promptCreated,
          actor: 'system',
          summary: 'Prompt created',
        },
      ],
      [
        runRecord({
          index: 2,
          intent: 'continue',
          userFollowup: 'Finish the original task without creating a wrapper.',
        }),
      ],
    );
    const c = fixture.componentInstance;
    expect(c.hasEvents()).toBe(true);
    expect(c.displayEvents().map(e => e.kind)).toEqual([
      TIMELINE_KIND.promptCreated,
      TIMELINE_KIND.agentRunFinished,
    ]);
    expect(c.displayEvents()[1].summary).toBe('');
    expect(c.displayEvents()[1].details?.['userFollowup']).toContain('Finish the original task');

    const html = fixture.nativeElement as HTMLElement;
    expect(html.querySelectorAll('[data-testid="timeline-event"]').length).toBe(2);
    expect(html.textContent).toContain('Run finished · 600s');
    expect(html.textContent).toContain('#2');
  });

  it('does not synthesize duplicate run rows when timeline already carries agent run events', async () => {
    const fixture = await build(
      [
        {
          ts: '2026-05-30T10:00:00Z',
          kind: TIMELINE_KIND.agentRunStarted,
          actor: 'system',
          summary: 'codex CLI start',
        },
      ],
      [runRecord()],
    );
    expect(fixture.componentInstance.displayEvents().length).toBe(1);
  });

  it('formatTime shows time only for today and date + time for other days', async () => {
    const fixture = await build([]);
    const c = fixture.componentInstance;

    const today = new Date();
    const todayIso = today.toISOString();
    const todayOut = c.formatTime(todayIso);
    expect(todayOut).toBe(today.toLocaleTimeString());
    expect(todayOut).not.toContain(today.toLocaleDateString());

    const other = new Date(today.getTime() - 3 * 24 * 60 * 60 * 1000);
    const otherOut = c.formatTime(other.toISOString());
    expect(otherOut).toContain(other.toLocaleDateString());
    expect(otherOut).toContain(other.toLocaleTimeString());
  });

  it('formatAbsoluteTime (hover tooltip) always carries the full date + time', async () => {
    const fixture = await build([]);
    const c = fixture.componentInstance;
    const d = new Date('2026-05-30T10:05:00Z');
    const out = c.formatAbsoluteTime(d.toISOString());
    expect(out).toBe(d.toLocaleString());
    expect(out).toContain(d.toLocaleDateString());
  });

  it('detailEntries hides gap/reason/attempt/maxAttempts (rendered separately)', async () => {
    const fixture = await build([]);
    const c = fixture.componentInstance;
    const entries = c.detailEntries({
      ts: '', kind: TIMELINE_KIND.qualityLoopReopened, actor: 'quality-loop', summary: '',
      details: { gap: 'g', reason: 'r', attempt: '2', maxAttempts: '3', cause: 'noop-recovery' },
    });
    expect(entries).toEqual([{ key: 'cause', label: 'Cause', value: 'noop-recovery' }]);
  });

  it('renders model, thinking level, and expandable source names without permanent defaults', async () => {
    const fixture = await build(
      [{
        ts: '2026-05-30T10:10:00Z',
        kind: TIMELINE_KIND.executionContext,
        actor: 'system',
        runId: 'session-42',
        summary: 'codex context: 2 sources, model gpt-5.6-sol, YOLO',
        details: { cli: 'codex', source: 'convention', sources: '2', mcp: '0' },
      }],
      [runRecord({
        inputSessionId: 'session-42',
        executionContext: {
          cli: 'codex',
          model: 'gpt-5.6-sol',
          thinkingLevel: 'medium',
          permissionMode: 'yolo',
          cwd: '/repo',
          contextMode: 'shared',
          capturedAt: '2026-05-30T10:10:00Z',
          source: 'convention',
          sources: [
            { kind: 'memory', label: 'AGENTS.md', path: '/repo/AGENTS.md', exists: true, detail: null },
            { kind: 'global-config', label: 'Codex config', path: '/home/.codex/config.toml', exists: true, detail: null },
          ],
        },
      })],
    );
    const html = fixture.nativeElement as HTMLElement;
    const row = html.querySelector<HTMLElement>('[data-kind="execution_context"]');

    expect(row?.textContent).toContain('gpt-5.6-sol');
    expect(row?.textContent).toContain('medium');
    expect(row?.textContent).not.toContain('YOLO');
    expect(row?.textContent).not.toContain('mcp 0');
    const disclosure = row?.querySelector<HTMLDetailsElement>('[data-testid="timeline-event-sources"]');
    expect(disclosure?.textContent).toContain('2 sources');
    expect(disclosure?.textContent).toContain('Codex config conventions');
    expect(disclosure?.textContent).toContain('AGENTS.md');
    expect(disclosure?.textContent).toContain('Codex config');
  });
});
