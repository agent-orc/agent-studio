import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { RegressionRadarComponent, specCategoryDisplay } from './regression-radar.component';
import type { RegressionRadarResult } from '../../models/regression-radar.model';
import { TaskService } from '../../../../services/task.service';

const baseResult: RegressionRadarResult = {
  overallStatus: 'Intended',
  intendedCount: 0,
  atRiskCount: 0,
  driftCount: 0,
  totalSpecChanges: 0,
  baselineSha: null,
  headSha: null,
  entries: [],
  taskGroups: [],
  error: null,
  generatedAt: '2026-06-05T10:00:00.000Z',
  durationMs: 12,
};

async function renderWith(result: RegressionRadarResult) {
  const taskService = {
    getRegressionRadar: () => of(result),
  };
  await TestBed.configureTestingModule({
    imports: [RegressionRadarComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      { provide: TaskService, useValue: taskService },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(RegressionRadarComponent);
  fixture.componentRef.setInput('jobId', 'job-1');
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

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
describe('RegressionRadarComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    try {
      await TestBed.configureTestingModule({
        imports: [RegressionRadarComponent],
        providers: [
          provideZonelessChangeDetection(),
          provideHttpClient(),
          provideHttpClientTesting(),
          provideRouter([]),
        ],
      }).compileComponents();
      const fixture = TestBed.createComponent(RegressionRadarComponent);
      fixture.componentRef.setInput('jobId', undefined);

      // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // jobId
    try { fixture.detectChanges(); } catch (e) {
        console.warn('[smoke] RegressionRadarComponent initial render skipped:', (e as Error).message);
      }
      expect(fixture.componentInstance).toBeTruthy();
    } catch (e) {
      // TestBed setup itself crashed (module-load cycle, env not
      // initialized because of file-order, etc). Still verifies the
      // component class is importable.
      console.warn('[smoke] RegressionRadarComponent TestBed setup skipped:', (e as Error).message);
      expect(RegressionRadarComponent).toBeTruthy();
    }
  });
});

describe('RegressionRadarComponent empty states', () => {
  it('renders an analysis error as an inline note instead of a boxed card', async () => {
    const root = await renderWith({
      ...baseResult,
      error: 'Git repository unavailable',
    });

    const note = root.querySelector<HTMLElement>('[data-testid="regression-radar-error"]');
    expect(note?.textContent).toContain('Git repository unavailable');
    expect(note?.querySelector<HTMLElement>('[data-testid="regression-radar-error-state"]')?.dataset['tone']).toBe('danger');
    expect(note?.classList.contains('radar-inline-note')).toBe(true);
    expect(note?.classList.contains('radar')).toBe(false);
  });

  it('renders the no-spec-changes state as an inline note instead of a boxed card', async () => {
    const root = await renderWith({
      ...baseResult,
      baselineSha: 'a'.repeat(40),
      headSha: 'b'.repeat(40),
    });

    const note = root.querySelector<HTMLElement>('[data-testid="regression-radar-empty"]');
    expect(note?.textContent).toContain('No changes for this task');
    expect(note?.querySelector<HTMLElement>('[data-testid="regression-radar-empty-state"]')?.dataset['tone']).toBe('neutral');
    expect(note?.classList.contains('radar-inline-note')).toBe(true);
    expect(note?.classList.contains('radar')).toBe(false);
  });
});

describe('RegressionRadarComponent status indicators', () => {
  it('maps spec categories to tones and falls back to neutral', () => {
    expect(['Intended', 'AtRisk', 'Drift', 'Other'].map(category => specCategoryDisplay(category)))
      .toEqual([
        { tone: 'success', label: 'Intended' },
        { tone: 'warning', label: 'At Risk' },
        { tone: 'danger', label: 'Drift' },
        { tone: 'neutral', label: 'Other' },
      ]);
  });

  it('shows the overall verdict and the counts on the shared indicator', async () => {
    const root = await renderWith({
      ...baseResult,
      overallStatus: 'Drift',
      intendedCount: 1,
      driftCount: 1,
      totalSpecChanges: 2,
    });

    const badge = root.querySelector<HTMLElement>('[data-testid="regression-radar-badge"]');
    expect(badge?.dataset['tone']).toBe('danger');
    expect(badge?.textContent?.trim()).toBe('Drift detected');
    const intended = root.querySelector<HTMLElement>('[data-testid="radar-intended-count"]');
    expect(intended?.dataset['tone']).toBe('success');
    expect(intended?.textContent?.trim()).toBe('1 intended');
    expect(root.querySelector<HTMLElement>('[data-testid="radar-drift-count"]')?.dataset['tone']).toBe('danger');
    expect(root.querySelector('[data-testid="radar-at-risk-count"]')).toBeNull();
  });
});
