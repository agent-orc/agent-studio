import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { FlowOverviewComponent } from './flow-overview.component';
import type { ProjectCycleTimeResponse } from '../../models/project-cycle-time.model';

describe('FlowOverviewComponent', () => {
  it('renders machine, throughput, tokens, cost and excluded time from one response', async () => {
    await TestBed.configureTestingModule({
      imports: [FlowOverviewComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(FlowOverviewComponent);
    fixture.componentRef.setInput('data', {
      coverage: { tasksInWindow: 2, reAcceptanceArtefactsInWindow: 1 },
      flow: [
        metric('machineCycle', 3600, 7200),
        metric('active', 1800, 3600),
        metric('resourceWait', 1800, 3600),
        metric('tokensPerCard', 12_500_000, 13_000_000),
        metric('costPerCardUsd', 5.17, 10),
        metric('humanWait', 600, 1200),
        metric('dependencyWait', 0, 0),
        metric('systemHold', 300, 600),
      ],
      steps: [{ id: 'coding', label: 'Coding', duration: metric('coding', 1800, 3600),
        tokens: 1_500_000_000, costUsd: 42.50, unpricedCalls: 1 }],
      resources: [{ id: 'reviewSlot', label: 'Review slot', duration: metric('reviewSlot', 900, 1800),
        shareOfMachineCycle: 0.25 }],
      throughputPerDay: [{ day: '2026-10-13T00:00:00Z', delivered: 3, machineDone: 2,
        completed: 2, reAcceptanceArtefacts: 1 }],
    } as ProjectCycleTimeResponse);
    fixture.detectChanges();
    await fixture.whenStable();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('12.5M');
    expect(text).toContain('1.5B');
    expect(text).toContain('$5.17');
    expect(text).toContain('$42.50');
    expect(text).toContain('1 re-acceptances excluded');
    expect(text).toContain('25% of machine cycle');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="flow-throughput-day"] tbody tr')?.textContent)
      .toContain('2026-10-13');
  });
});

function metric(stage: string, p50: number, p90: number) {
  return { stage, label: stage, kind: 'flow', unit: 'seconds', highlighted: false,
    count: 2, p50, p90, max: p90, mean: p50, total: p50 * 2 };
}
