import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import type { TokenTimelineModelUsage } from '../../models/tokens.model';
import { WorkspaceTokenModelTableComponent } from './workspace-token-model-table.component';

const row = (project: string, model: string, modelLabel: string, host: string, total: number): TokenTimelineModelUsage => ({
  project, model, modelLabel, host, cliTypes: ['claude'], calls: 1,
  input: total, output: 0, cacheRead: 0, cacheWrite: 0, total, dollars: null, allModelsPriced: false,
});

describe('WorkspaceTokenModelTableComponent (AGT-2986)', () => {
  async function render(hidden: string[] = []) {
    await TestBed.configureTestingModule({
      imports: [WorkspaceTokenModelTableComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkspaceTokenModelTableComponent);
    fixture.componentRef.setInput('models', [
      row('studio', 'zeta-model-9', 'zeta-model-9', 'agent-runner-01', 158_000_000),
      row('studio', 'claude-sonnet-5', 'Claude Sonnet 5', 'local', 2_000_000),
      row('website', 'gpt-6-sol', 'gpt-6-sol', 'agent-runner-01', 40_000_000),
    ]);
    fixture.componentRef.setInput('hiddenProjects', new Set(hidden));
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }

  it('lists remote and local rows and sums the visible rows', async () => {
    const fixture = await render();
    const component = fixture.componentInstance;
    expect(component.visibleModels().map(m => m.host)).toEqual(['agent-runner-01', 'local', 'agent-runner-01']);
    expect(component.totals().total).toBe(200_000_000);
    const el: HTMLElement = fixture.nativeElement;
    const row = el.querySelector('[data-testid="wtt-model-row-studio|zeta-model-9|agent-runner-01"]');
    // An id the registry does not label renders as the id.
    expect(row?.querySelector('[data-testid="wtt-model-label"]')?.textContent?.trim()).toBe('zeta-model-9');
  });

  it('drops hidden projects from rows and totals', async () => {
    const fixture = await render(['website']);
    expect(fixture.componentInstance.visibleModels().map(m => m.project)).toEqual(['studio', 'studio']);
    expect(fixture.componentInstance.totals().total).toBe(160_000_000);
  });
});
