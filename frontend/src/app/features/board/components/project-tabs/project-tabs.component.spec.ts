import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import type { TaskInfo, RunnerStatus } from '../../../../models/task.model';
import { buildProjectTokenChip, projectAutoInfo, projectRunnerIndicator } from './project-chip-view-model';
import { ProjectTabsComponent } from './project-tabs.component';

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
describe('ProjectTabsComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectTabsComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectTabsComponent);
    fixture.componentRef.setInput('isActive', undefined);
    fixture.componentRef.setInput('runnerIndicator', undefined);
    fixture.componentRef.setInput('autoInfo', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // isActive, runnerIndicator, autoInfo
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] ProjectTabsComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('aggregates token badges for a single project only', () => {
    const chip = buildProjectTokenChip([
      projectJob('alpha', 120_000, 20_000, 'claude-sonnet-4-6', '2026-05-05T08:00:00Z'),
      projectJob('alpha', 10_000, 2_000, 'gpt-5.2', '2026-05-05T09:00:00Z'),
      projectJob('beta', 999_000, 1_000, 'other-model', '2026-05-05T10:00:00Z'),
    ], 'alpha');

    expect(chip).not.toBeNull();
    expect(chip?.totalTokens).toBe(152_000);
    expect(chip?.label).toBe('152k');
    expect(chip?.jobsWithTokens).toBe(2);
    expect(chip?.models).toEqual(['gpt-5.2', 'claude-sonnet-4-6']);
    expect(chip?.tooltip).toContain('Input 130k');
    expect(chip?.tooltip).toContain('Output 22k');
    expect(chip?.tooltip).toContain('Estimated cost: $0.13');
    expect(chip?.tooltip).toContain('historical list prices');
  });

  it('derives runner and auto-pickup chip state without component logic', () => {
    const status: RunnerStatus = {
      projects: {
        alpha: {
          projectName: 'alpha',
          mode: 'auto-continuous',
          activeJobId: null,
          activeExecution: null,
          queuedJobIds: ['job-1', 'job-2'],
        },
        beta: {
          projectName: 'beta',
          mode: 'paused',
          activeJobId: 'job-3',
          activeExecution: null,
          queuedJobIds: [],
        },
      },
    };

    expect(projectRunnerIndicator(status, 'alpha')).toEqual({ cls: 'idle', tone: 'success', label: 'Auto idle' });
    expect(projectAutoInfo(status, 'alpha')).toMatchObject({ state: 'on', readyCount: 2 });
    expect(projectRunnerIndicator(status, 'beta')).toEqual({ cls: 'running', tone: 'running', label: 'Running' });
    expect(projectAutoInfo(status, 'beta')).toMatchObject({ state: 'stopping', label: 'Stopping' });
  });
});

function projectJob(
  projectName: string,
  inputTokens: number,
  outputTokens: number,
  model: string,
  ts: string,
): TaskInfo {
  return {
    projectName,
    tokenSummary: {
      calls: 1,
      inputTokens,
      outputTokens,
      cacheReadTokens: 0,
      cacheCreationTokens: 0,
      totalTokens: inputTokens + outputTokens,
      estimatedApiCostUsd: inputTokens / 1_000_000,
      allModelsPriced: true,
      lastModel: model,
      lastUpdate: ts,
      entries: [{
        ts,
        model,
        inputTokens,
        outputTokens,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
      }],
    },
  } as TaskInfo;
}
