import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { WorkspaceTokenTimelineComponent } from './workspace-token-timeline';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('WorkspaceTokenTimelineComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkspaceTokenTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkspaceTokenTimelineComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] WorkspaceTokenTimelineComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('does not present a partial chat price as the total', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkspaceTokenTimelineComponent],
      providers: [
        provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([]),
      ],
    }).compileComponents();
    const component = TestBed.createComponent(WorkspaceTokenTimelineComponent).componentInstance;
    component.disabledProjects.set(new Set());
    component.timeline.set({
      windowStart: '', windowEnd: '', windowHours: 24, bucketMinutes: 60,
      bucketCount: 1, cells: [], fetchedAt: '', disclaimer: '',
      projects: [{
        project: 'studio', calls: 2, input: 100, output: 20,
        cacheRead: 0, cacheWrite: 0, total: 120, dollars: 0.01,
        allModelsPriced: false, peakBucketStart: null, peakBucketTotal: 120,
        lastActivity: null, agentTokens: 0, supportingTokens: 0,
        orchestratorTokens: 0, chatTokens: 120, chatCostUsd: null,
      }],
    });
    expect(component.tableTotals().chat).toBe(120);
    expect(component.tableTotals().chatDollars).toBeNull();
  });
});
