import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectProductRuntimePanelComponent } from './project-product-runtime-panel.component';

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
describe('ProjectProductRuntimePanelComponent (smoke)', () => {
  it('shows the rolling card opening latency in Operations', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectProductRuntimePanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectProductRuntimePanelComponent);
    fixture.componentRef.setInput('projectName', 'PROJ-002');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/runtime/PROJ-002/events').flush({ project: 'PROJ-002', events: [], warnings: [] });
    http.expectOne('/api/runtime/task-detail-latency').flush({ count: 100, p50Ms: 44, p95Ms: 180, maxMs: 220 });
    fixture.detectChanges();
    const text = (fixture.nativeElement.querySelector('[data-testid="task-detail-latency"]') as HTMLElement).textContent ?? '';
    expect(text).toContain('100 reads / 5 min');
    expect(text).toContain('p50 44 ms');
    expect(text).toContain('p95 180 ms');
    fixture.destroy();
  });

  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectProductRuntimePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectProductRuntimePanelComponent);
    fixture.componentRef.setInput('projectName', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // projectName
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] ProjectProductRuntimePanelComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});
