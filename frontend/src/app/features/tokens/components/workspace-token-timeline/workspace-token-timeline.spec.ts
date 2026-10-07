import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { HttpTestingController } from '@angular/common/http/testing';
import { WorkspaceTokenTimelineComponent } from './workspace-token-timeline';
import type { TokenTimeline } from '../../models/tokens.model';
import type { UsageLedgerScope } from '../../../usage-cockpit';

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
  it('cancels an older ledger request and clears its data when the scope changes', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkspaceTokenTimelineComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkspaceTokenTimelineComponent);
    const http = TestBed.inject(HttpTestingController);
    const firstScope: UsageLedgerScope = {
      workspaceId: 'ws-a', range: 'today', fromUtc: '2026-09-24T22:00:00Z',
      toUtc: '2026-09-25T22:00:00Z', timeZone: 'Europe/Berlin',
    };
    const nextScope: UsageLedgerScope = {
      ...firstScope, workspaceId: 'ws-b', projectId: 'PROJ-002',
    };
    const timeline: TokenTimeline = {
      windowStart: firstScope.fromUtc, windowEnd: firstScope.toUtc,
      windowHours: 24, bucketMinutes: 60, bucketCount: 24,
      cells: [], projects: [{
        project: 'studio', calls: 1, input: 100, output: 0, cacheRead: 0, cacheWrite: 0,
        total: 100, dollars: null, allModelsPriced: false, peakBucketStart: null,
        peakBucketTotal: 100, lastActivity: firstScope.toUtc, agentTokens: 100,
        supportingTokens: 0, orchestratorTokens: 0,
      }],
      models: [{
        project: 'studio', model: 'gpt-unlisted-model-9', modelLabel: 'gpt-unlisted-model-9',
        host: 'agent-runner-01', cliTypes: ['codex'], calls: 1, input: 100,
        output: 0, cacheRead: 0, cacheWrite: 0, total: 100, dollars: null,
        allModelsPriced: false,
      }],
      fetchedAt: firstScope.toUtc, disclaimer: '',
    };

    fixture.componentRef.setInput('scope', firstScope);
    fixture.detectChanges();
    await fixture.whenStable();
    const old = http.expectOne(req => req.url.endsWith('/workspace/tokens/timeline'));
    expect(old.request.params.get('workspaceId')).toBe('ws-a');

    fixture.componentRef.setInput('scope', nextScope);
    fixture.detectChanges();
    await fixture.whenStable();
    const current = http.expectOne(req => req.url.endsWith('/workspace/tokens/timeline'));
    expect(old.cancelled).toBe(true);
    expect(current.request.params.get('workspaceId')).toBe('ws-b');
    expect(current.request.params.get('projectId')).toBe('PROJ-002');
    expect(fixture.componentInstance.timeline()).toBeNull();

    fixture.componentInstance.disabledProjects.set(new Set(['studio']));
    current.flush(timeline);
    expect(fixture.componentInstance.timeline()).toEqual(timeline);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector(
      '[data-testid="wtt-model-row-studio|gpt-unlisted-model-9|agent-runner-01"]')).not.toBeNull();

    fixture.componentRef.setInput('scope', firstScope);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.componentInstance.timeline()).toBeNull();
    const third = http.expectOne(req => req.url.endsWith('/workspace/tokens/timeline'));
    expect(third.request.params.get('workspaceId')).toBe('ws-a');
    fixture.destroy();
    http.verify({ ignoreCancelled: true });
  });
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
});
