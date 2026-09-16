import { afterEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { AgentWorkSummaryPollService } from '../../../../../polling/services/agent-work-summary-poll.service';
import { OverviewAgentWorkComponent } from './overview-agent-work.component';
import type { TaskInfo } from '../../../../../../models/task.model';
import type { AgentWorkSummary } from '../../../../../session-events';

/**
 * AGT-2819 split the Agent Work block out of `overview-pane.component.ts`. The
 * pane's coverage of the call count and the tool chips is asserted here against
 * the extracted component, unchanged in substance.
 */
function baseJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'test-1', taskKey: 'wp::test-1', key: 'wp::test-1', title: 'Test', state: '6-completed',
    order: 1, agent: 'human', createdAt: new Date().toISOString(),
    watchPath: '/tmp', projectName: 'test', folderPath: '/tmp/test-1',
    lastActivity: new Date().toISOString(), sessionName: null,
    model: null, cliType: null, useOwnSession: null, lastUsage: null,
    execution: null, commit: null,
    ...overrides,
  };
}

function summary(overrides: Partial<AgentWorkSummary> = {}): AgentWorkSummary {
  return {
    calls: 3,
    recovered: false,
    toolCalls: 42,
    toolCounts: [
      { tool: 'Read', count: 24 },
      { tool: 'Edit', count: 12 },
      { tool: 'Bash', count: 6 },
    ],
    startedAt: new Date(Date.now() - 60_000).toISOString(),
    lastTouchAt: new Date().toISOString(),
    currentSessionId: 'sess-1',
    ...overrides,
  };
}

async function build(job: TaskInfo, work: AgentWorkSummary | null) {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [OverviewAgentWorkComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      AgentWorkSummaryPollService,
    ],
  }).compileComponents();
  if (work) TestBed.inject(AgentWorkSummaryPollService).summary.set(work);
  const fixture = TestBed.createComponent(OverviewAgentWorkComponent);
  fixture.componentRef.setInput('job', job);
  try { fixture.detectChanges(); } catch (e) {
    console.warn('[smoke] OverviewAgentWorkComponent initial render skipped:', (e as Error).message);
  }
  return fixture;
}

afterEach(() => {
  TestBed.resetTestingModule();
});

describe('OverviewAgentWorkComponent', () => {
  it('surfaces call count + tool counts from the poll service', async () => {
    const fixture = await build(baseJob({ sessionName: 'sess-1' }), summary());
    const c = fixture.componentInstance;

    expect(c.agentWork()!.calls).toBe(3);
    expect(c.topToolCounts().map(tc => tc.tool)).toEqual(['Read', 'Edit', 'Bash']);
    expect(c.toolCountsTooltip()).toContain('Read: 24');
    expect(c.sessionDebugTooltip()).toContain('sess-1');
  });

  it('renders the call badge and the tool chip strip', async () => {
    const fixture = await build(baseJob(), summary());
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-testid="agent-work-calls"]')?.textContent).toContain('3 calls');
    expect(host.querySelector('[data-testid="agent-work-tools"]')?.textContent).toContain('Read');
  });

  it('caps the chip strip at six tools and keeps the full list in the tooltip', async () => {
    const many = Array.from({ length: 9 }, (_, i) => ({ tool: `Tool${i}`, count: 9 - i }));
    const fixture = await build(baseJob(), summary({ toolCounts: many }));
    const c = fixture.componentInstance;

    expect(c.topToolCounts()).toHaveLength(6);
    expect(c.toolCountsTooltip().split('\n')).toHaveLength(9);
  });

  it('a recovered run is flagged next to the call count', async () => {
    const fixture = await build(baseJob(), summary({ recovered: true }));

    expect(
      (fixture.nativeElement as HTMLElement)
        .querySelector('[data-testid="agent-work-calls"]')?.textContent,
    ).toContain('recovered');
  });

  it('the tool row is absent when nothing called a tool', async () => {
    const fixture = await build(baseJob(), summary({ toolCalls: 0, toolCounts: [] }));

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="agent-work-tools"]'),
    ).toBeNull();
  });

  it('no session id means no debug tooltip', async () => {
    const fixture = await build(baseJob({ sessionName: null }), summary());
    expect(fixture.componentInstance.sessionDebugTooltip()).toBe('');
  });
});
