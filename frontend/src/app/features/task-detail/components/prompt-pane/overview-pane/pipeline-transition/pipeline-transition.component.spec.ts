import { describe, expect, it } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { PipelineTransitionComponent } from './pipeline-transition.component';
import type { PipelineRowVm } from '../pipeline-row.vm';

describe('PipelineTransitionComponent', () => {
  it('shows the deciding model, unpriced cost, occurrence count and evidence', async () => {
    await TestBed.configureTestingModule({
      imports: [PipelineTransitionComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PipelineTransitionComponent);
    fixture.componentRef.setInput('row', {
      id: 'post-orchestrator-decision', label: 'Final decision', verdict: 'accept',
      status: 'passed', model: 'gpt-unknown', modelSource: 'project',
      durationMs: 1520, costUsd: 0, totalTokens: 120, unpricedRuns: 1,
      costStatus: 'unpriced', occurrences: 3,
    } as PipelineRowVm);
    fixture.componentRef.setInput('artefact', 'decision.md');
    let requested = '';
    fixture.componentInstance.documentRequested.subscribe(file => { requested = file; });
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('Final decision → accept');
    expect(host.textContent).toContain('gpt-unknown · project');
    expect(host.textContent).toContain('no price data');
    expect(host.textContent).toContain('3 runs');
    host.querySelector('button')!.click();
    expect(requested).toBe('decision.md');
  });
});
