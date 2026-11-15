import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { AgentWorkDetailComponent } from './agent-work-detail.component';
import { TaskService } from '../../../../../services/task.service';
import type { AgentWorkDetail } from '../../../../session-events';

const SAMPLE: AgentWorkDetail = {
  totalCalls: 3,
  groups: [
    {
      tool: 'Bash',
      count: 2,
      calls: [
        { ts: '2026-06-04T10:00:00Z', argument: 'npm test', completed: true, isError: false, resultFirstLine: 'PASS' },
        { ts: '2026-06-04T10:01:00Z', argument: 'git status', completed: true, isError: true, resultFirstLine: 'fatal: not a repo' },
      ],
    },
    {
      tool: 'Read',
      count: 1,
      calls: [
        { ts: '2026-06-04T10:02:00Z', argument: 'src/app.ts', completed: false, isError: null, resultFirstLine: null },
      ],
    },
  ],
};

function setup(detail: AgentWorkDetail = SAMPLE) {
  const getAgentWorkDetail = vi.fn().mockReturnValue(of(detail));
  TestBed.configureTestingModule({
    imports: [AgentWorkDetailComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: { getAgentWorkDetail } },
    ],
  });
  const fixture = TestBed.createComponent(AgentWorkDetailComponent);
  fixture.componentRef.setInput('jobId', 'job-1');
  fixture.detectChanges();
  return { fixture, getAgentWorkDetail };
}

const html = (fixture: { nativeElement: HTMLElement }) =>
  (fixture.nativeElement as HTMLElement);

describe('AgentWorkDetailComponent', () => {
  it('is open by default and fetches the grouped detail once', async () => {
    const { fixture, getAgentWorkDetail } = setup();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(getAgentWorkDetail).toHaveBeenCalledTimes(1);
    expect(html(fixture).querySelector('[data-testid="agent-work-detail-body"]')).not.toBeNull();
  });

  it('renders one group per tool on mount', async () => {
    const { fixture, getAgentWorkDetail } = setup();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(getAgentWorkDetail).toHaveBeenCalledTimes(1);
    const groups = html(fixture).querySelectorAll('[data-testid="agent-work-detail-group"]');
    expect(groups.length).toBe(2);
    expect(groups[0].textContent).toContain('Bash');
    expect(groups[0].textContent).toContain('2');
  });

  it('does not refetch when toggled closed and open again', async () => {
    const { fixture, getAgentWorkDetail } = setup();
    await fixture.whenStable();
    fixture.componentInstance.toggleOpen(); // close
    fixture.componentInstance.toggleOpen(); // open again
    await fixture.whenStable();
    expect(getAgentWorkDetail).toHaveBeenCalledTimes(1);
  });

  it('expands a group to reveal its per-call arguments', async () => {
    const { fixture } = setup();
    await fixture.whenStable();
    fixture.componentInstance.toggleGroup('Bash');
    fixture.detectChanges();

    const calls = html(fixture).querySelectorAll('[data-testid="agent-work-detail-call"]');
    expect(calls.length).toBe(2);
    expect(calls[0].textContent).toContain('npm test');
    expect(calls[1].textContent).toContain('git status');
    // The errored call carries the error modifier.
    expect(calls[1].className).toContain('awd-call--error');
  });

  it('only one group is expanded at a time', () => {
    const { fixture } = setup();
    const c = fixture.componentInstance;
    c.toggleGroup('Bash');
    expect(c.isExpanded('Bash')).toBe(true);
    c.toggleGroup('Read');
    expect(c.isExpanded('Bash')).toBe(false);
    expect(c.isExpanded('Read')).toBe(true);
  });

  it('shows an empty note when there are no groups', async () => {
    const { fixture } = setup({ groups: [], totalCalls: 0 });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(html(fixture).querySelector('[data-testid="agent-work-detail-empty"]')).not.toBeNull();
    expect(html(fixture).querySelector('[data-testid="agent-work-detail-empty-state"]')?.getAttribute('data-tone')).toBe('neutral');
  });

  it('builds an HTML tooltip with the argument and result line', () => {
    const { fixture } = setup();
    const tip = fixture.componentInstance.callTooltip({
      ts: null, argument: 'rm -rf <dir>', completed: true, isError: true, resultFirstLine: 'denied',
    });
    expect(tip).toContain('<code>rm -rf &lt;dir&gt;</code>');
    expect(tip).toContain('denied');
  });
});
