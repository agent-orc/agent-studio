import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ResultViewComponent } from './result-view.component';
import type { TaskDetail } from '../../../../../models/task.model';
import type { ProtocolVerdict } from '../protocol-verdict';

function verdict(overrides: Partial<ProtocolVerdict> = {}): ProtocolVerdict {
  return {
    kind: 'ok',
    status: 'succeeded',
    signals: [],
    emoji: '🟢',
    label: 'Success',
    detail: 'Last run completed successfully.',
    duration: '4 min',
    ...overrides,
  };
}

function detail(statusMarkdown: string, overrides: Record<string, unknown> = {}): TaskDetail {
  return {
    info: {
      id: 'job-1',
      watchPath: '/ws',
      title: 'Fix the broken thing',
      taskType: 'bug',
      mode: 'coding',
      tags: ['code-review:grade-a'],
      tokenSummary: { totalTokens: 12_000 },
      commits: [{}],
      codeActivityDetected: true,
      ...overrides,
    },
    statusMarkdown,
  } as unknown as TaskDetail;
}

// A status.md with only the header + overview, so the detail layer (and the
// heavier beautiful-results renderer) stays out of the DOM for the head/overview
// assertions.
const HEAD_ONLY = `# Status

- Result: Success
- Duration: 4 min

## Overview
- Problem: The card was unreadable.
- Solution: Re-toned the surface.
`;

const SCAFFOLD = `<!-- agent-studio:result-scaffold -->
<!-- agent-studio:operator-result-backfill -->
# Status

- Result: Success
- Case: generic
- Deliverables: [results/report.html](results/report.html)
- Provenance: Synthesized by Agent Studio because no generated status.md was available.

## Overview
- Problem: \`status.md\` was missing for \`C:\\Projects\\workspace::raw-job-id\`.
- Solution: This honest scaffold exposes the recorded outcome and existing evidence.

## What Was Done
- The task reached \`5-human-review\`.

## Open Items
- None recorded in this synthesized scaffold.
`;

async function build(d: TaskDetail, v: ProtocolVerdict) {
  await TestBed.configureTestingModule({
    imports: [ResultViewComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ResultViewComponent);
  fixture.componentRef.setInput('detail', d);
  fixture.componentRef.setInput('verdict', v);
  fixture.detectChanges();
  return fixture;
}

describe('ResultViewComponent', () => {
  it('renders the case badge and the overview problem/solution', async () => {
    const fixture = await build(detail(HEAD_ONLY), verdict());
    const el = fixture.nativeElement as HTMLElement;

    const badge = el.querySelector('[data-testid="result-case-badge"]');
    expect(badge?.textContent).toContain('Success');

    expect(el.querySelector('[data-testid="result-overview-problem"]')?.textContent).toContain('unreadable');
    expect(el.querySelector('[data-testid="result-overview-solution"]')?.textContent).toContain('Re-toned');
  });

  it.each([
    ['not-applicable', 'No build/test defined'],
    ['not-proven', 'Build/test gate skipped at d1649ce9'],
  ] as const)('renders build gate evidence state %s consistently in Result', async (state, summary) => {
    const fixture = await build(detail(HEAD_ONLY, {
      state: '5-human-review',
      testEvidence: {
        runId: null,
        runCommit: null,
        runState: null,
        runResult: null,
        matchQuality: 'perfect',
        direction: 'exact',
        distance: 0,
        diffContained: true,
        evidenceState: state,
        awaitingEvidence: false,
        summary,
        sources: [{
          kind: 'build-test-gate',
          id: 'gate-42',
          commit: 'd1649ce9',
          result: state,
          observedAt: null,
          summary,
          reason: state === 'not-proven' ? 'The build command did not run.' : 'No build/test commands are defined.',
          reportRef: 'post-steps/build-test-gate-1.log',
        }],
      },
    }), verdict());

    const status = fixture.nativeElement.querySelector('[data-testid="result-test-evidence"]') as HTMLElement;
    expect(status.dataset['evidenceState']).toBe(state);
    expect(status.textContent).toContain(summary);
  });

  it('renders one verdict plus grade, duration and tokens metrics', async () => {
    const fixture = await build(detail(HEAD_ONLY), verdict());
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelectorAll('[data-testid="result-case-badge"]')).toHaveLength(1);
    expect(el.querySelector('[data-testid="result-metric-verdict"]')).toBeNull();
    expect(el.querySelector('[data-testid="result-metric-grade"]')?.textContent).toContain('Grade A');
    expect(el.querySelector('[data-testid="result-metric-duration"]')?.textContent).toContain('4m');
    expect(el.querySelector('[data-testid="result-metric-tokens"]')?.textContent).toContain('12.0k tokens');
    expect(el.querySelector('[data-testid="result-case-dot"]')).not.toBeNull();
  });

  it('emits grade navigation and keeps provenance in the same head row', async () => {
    const fixture = await build(detail(HEAD_ONLY), verdict());
    fixture.componentRef.setInput('provenance', {
      label: 'claude / haiku', tooltip: 'summary details', producer: 'claude / haiku',
      model: 'haiku', cli: 'claude', tokens: '1k tokens', duration: '2s',
    });
    fixture.detectChanges();
    let navigated = '';
    fixture.componentInstance.navigateMetric.subscribe((id) => navigated = id);

    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('[data-testid="result-metric-grade"]') as HTMLButtonElement).click();
    expect(navigated).toBe('grade');
    expect(el.querySelector('[data-testid="result-view"] [data-testid="protocol-provenance"]')?.textContent)
      .toContain('Generated by');
  });

  it('renders compact files + tests stats when the header carries the metrics', async () => {
    const md = `# Status\n\n- Result: Success\n- Files: 4\n- Tests: 12 passed\n\n## Overview\n- Problem: x\n- Solution: y\n`;
    const fixture = await build(detail(md), verdict());
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="result-metric-files"]')?.textContent).toContain('4 files');
    expect(el.querySelector('[data-testid="result-metric-tests"]')?.textContent).toContain('12 ✓');
  });

  it('marks a synthesized overview so the origin is honest', async () => {
    const legacy = `# Status\n\n- Result: Success\n\n## What Was Done\n- Did the thing.\n`;
    const fixture = await build(detail(legacy), verdict({ duration: null }));
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="result-overview-synthesized"]')).not.toBeNull();
  });

  it('renders the detail layer when there is body content beyond the overview', async () => {
    const full = `${HEAD_ONLY}\n## What Was Done\n- Edited a file.\n`;
    const fixture = await build(detail(full), verdict());
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="result-detail"]')).not.toBeNull();
  });

  it('reflects the blocked case tone when the run did not land', async () => {
    const fixture = await build(detail(HEAD_ONLY), verdict({ kind: 'problem', label: 'Blocked', emoji: '🔴' }));
    const el = fixture.nativeElement as HTMLElement;
    const article = el.querySelector('[data-testid="result-view"]');
    expect(article?.getAttribute('data-case')).toBe('blocked');
    expect(el.querySelector('[data-testid="result-case-badge"]')?.textContent).toContain('Blocked');
  });

  it('applies the per-case overview layout and renders a connector between the two lines', async () => {
    // taskType bug -> bugfix -> sequence layout.
    const fixture = await build(detail(HEAD_ONLY), verdict());
    const el = fixture.nativeElement as HTMLElement;
    const overview = el.querySelector('[data-testid="result-overview"]');
    expect(overview?.getAttribute('data-layout')).toBe('sequence');
    // Both problem and solution present -> the flow connector renders.
    expect(el.querySelector('[data-testid="result-overview-connector"]')).not.toBeNull();
  });

  it('uses the blocker layout for a run that did not land', async () => {
    const fixture = await build(detail(HEAD_ONLY), verdict({ kind: 'problem', label: 'Blocked', emoji: '🔴' }));
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="result-overview"]')?.getAttribute('data-layout')).toBe('blocker');
  });

  it('shows the case intent blurb when there is no detail body', async () => {
    const fixture = await build(detail(HEAD_ONLY), verdict());
    const el = fixture.nativeElement as HTMLElement;
    // HEAD_ONLY has no body beyond the overview, so the blurb leads.
    expect(el.querySelector('[data-testid="result-overview-blurb"]')).not.toBeNull();
  });

  it('renders one scaffold origin notice and keeps only the evidence detail', async () => {
    const fixture = await build(detail(SCAFFOLD, {
      key: 'AGT-2514',
      taskKey: 'C:\\Projects\\workspace::raw-job-id',
      lastActivity: '2026-08-08T21:59:26.180Z',
    }), verdict());
    const el = fixture.nativeElement as HTMLElement;

    const notice = el.querySelector('[data-testid="result-scaffold-notice"]');
    expect(notice?.textContent).toContain(
      'The run did not write status.md. This report was generated automatically from task.json and the artifacts.',
    );
    expect(notice?.textContent).toContain('2026-08-08T21:59:26.180Z');
    expect(notice?.textContent).toContain('AGT-2514');
    expect(el.querySelector('[data-testid="result-overview"]')).toBeNull();
    expect(el.querySelector('[data-testid="result-detail"]')?.textContent).toContain('What Was Done');
    expect(el.querySelector('[data-testid="result-detail"]')?.textContent).toContain('Open Items');
    expect(el.textContent).not.toContain('C:\\Projects');
    expect(el.textContent).not.toContain('This honest scaffold exposes');
    expect(el.textContent).not.toContain('agent-studio:result-scaffold');
    expect(el.textContent).not.toContain('agent-studio:operator-result-backfill');
  });

  it('opens the task artifacts from the scaffold link', async () => {
    const fixture = await build(detail(SCAFFOLD, {
      key: 'AGT-2514',
      lastActivity: '2026-08-08T21:59:26.180Z',
    }), verdict());
    let metric = '';
    fixture.componentInstance.navigateMetric.subscribe((id) => metric = id);

    const link = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLAnchorElement>('a[href="#artifacts"]');
    link?.click();

    expect(metric).toBe('artifacts');
  });
});
