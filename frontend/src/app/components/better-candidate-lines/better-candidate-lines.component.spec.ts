import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { BetterCandidateLinesComponent } from './better-candidate-lines.component';

describe('BetterCandidateLinesComponent', () => {
  it('renders one linked informational line with benchmark deltas and evidence age', async () => {
    await TestBed.configureTestingModule({
      imports: [BetterCandidateLinesComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(BetterCandidateLinesComponent);
    fixture.componentRef.setInput('note', {
      currentModel: 'gpt-5.6-sol',
      currentThinkingLevel: 'max',
      capabilityClass: 'CodingAgent',
      evidenceSnapshot: 'snapshot',
      evaluatedAtUtc: '2026-09-13T08:00:00Z',
      matrixUrl: 'https://agent-orchestrator.dev/token-economy/model-benchmarks/',
      candidates: [{
        model: 'gpt-6-astra',
        thinkingLevel: null,
        benchmarkType: 'deepswe-v1.1',
        benchmarkName: 'DeepSWE v1.1',
        scoreDelta: 1.1,
        costDeltaUsd: -4.96,
        evidenceAgeDays: 10,
        evidenceStale: false,
      }],
    });
    fixture.detectChanges();

    const line = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="better-candidate-gpt-6-astra"]',
    ) as HTMLAnchorElement;
    expect(line.textContent).toContain('gpt-6-astra/default');
    expect(line.textContent).toContain('deepswe-v1.1');
    expect(line.textContent).toContain('SΔ+1.10');
    expect(line.textContent).toContain('$Δ-4.96');
    expect(line.textContent).toContain('age 10d');
    expect(line.href).toContain('/token-economy/model-benchmarks/');
  });
});
