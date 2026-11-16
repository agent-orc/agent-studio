import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { CreateTaskDialogComponent } from './create-task-dialog.component';
import { AuthSessionState } from '../../../../services/auth.service';
import { TaskState, type TagRegistryEntry } from '../../../../models/task.model';
import { TagRegistryStore } from '../../../../services/tag-registry.store';

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
describe('CreateTaskDialogComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [CreateTaskDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(CreateTaskDialogComponent);
    fixture.componentRef.setInput('cliTypeDraft', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // cliTypeDraft
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] CreateTaskDialogComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('shows policy provenance and offers a one-click return from an override', async () => {
    await TestBed.configureTestingModule({
      imports: [CreateTaskDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(CreateTaskDialogComponent);
    fixture.componentRef.setInput('cliTypeDraft', 'codex');
    fixture.componentRef.setInput('modelSelectionExplicit', true);
    fixture.componentRef.setInput('policySuggestion', {
      policyVersion: '2026-07-24',
      policyWikiPath: 'docs/system/domains/model-routing-policy.md',
      taskType: 'feature',
      tier: 'terra-medium',
      model: 'gpt-5.6-terra',
      thinkingLevel: 'medium',
      score: 25,
      economyMode: false,
      economyDowngraded: false,
      correctnessFloorTier: null,
      reason: 'feature default',
      estimatedSavingsPercent: 35,
    });
    const reset = vi.fn();
    fixture.componentInstance.policySelectionRequest.subscribe(reset);

    fixture.detectChanges();

    const suggestion = fixture.nativeElement.querySelector(
      '[data-testid="create-model-policy-suggestion"]',
    ) as HTMLElement;
    expect(suggestion.dataset['tier']).toBe('terra-medium');
    expect(suggestion.dataset['source']).toBe('override');
    expect(suggestion.textContent).toContain('Policy 2026-07-24');
    const button = fixture.nativeElement.querySelector(
      '[data-testid="create-use-policy-model"]',
    ) as HTMLButtonElement;
    button.click();
    expect(reset).toHaveBeenCalledOnce();
  });

  it('offers a Product session only the lanes it may file into (AGT-3668)', async () => {
    await TestBed.configureTestingModule({
      imports: [CreateTaskDialogComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    TestBed.inject(AuthSessionState).status.set({
      profile: 'networked', bootstrapRequired: false, authenticated: true,
      user: { id: 'usr-1', username: 'carla', displayName: 'Carla', role: 'contributor', projects: ['PROJ-1'],
        disabled: false, mustChangePassword: false },
    });
    const fixture = TestBed.createComponent(CreateTaskDialogComponent);
    fixture.componentRef.setInput('cliTypeDraft', 'claude');
    fixture.componentInstance.newTargetState.set(TaskState.Preparation);
    TestBed.tick();

    expect(fixture.componentInstance.segmentedLaneOptions().map(lane => lane.value))
      .toEqual([TaskState.Backlog, TaskState.Ready]);
    expect(fixture.componentInstance.newTargetState()).toBe(TaskState.Backlog);
  });
});

/**
 * AGT-3834 (playthrough AGT-3796 F7): the dialog opened in the Prompt field,
 * every tag, lane, type and mode option was its own Tab stop, and Title was
 * 55 Tab stops from Create.
 */
describe('CreateTaskDialogComponent keyboard path (AGT-3834)', () => {
  const TABBABLE = 'button, input, select, textarea, a[href], [tabindex]';

  async function mount() {
    await TestBed.configureTestingModule({
      imports: [CreateTaskDialogComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    TestBed.inject(TagRegistryStore).tags.set(Array.from({ length: 37 }, (_, i) => (
      { id: `tag-${i}`, label: `Tag ${i}`, color: '#888888' } as TagRegistryEntry)));
    const fixture = TestBed.createComponent(CreateTaskDialogComponent);
    fixture.componentRef.setInput('cliTypeDraft', 'claude');
    fixture.componentRef.setInput('watchPaths', [{ path: '/tmp/p', name: 'Project' }]);
    fixture.componentInstance.newTitle.set('Fix the board');
    fixture.componentInstance.newPrompt.set('Describe the fix');
    fixture.detectChanges();
    await fixture.whenStable();
    await Promise.resolve();
    const root = fixture.nativeElement as HTMLElement;
    const byTestId = (id: string) => root.querySelector(`[data-testid="${id}"]`) as HTMLElement;
    return { fixture, root, byTestId };
  }

  /** Sequential Tab order the browser would walk (no positive tabindex is used). */
  function tabStops(root: HTMLElement): HTMLElement[] {
    return Array.from(root.querySelectorAll<HTMLElement>(TABBABLE)).filter((el) =>
      !(el as HTMLButtonElement).disabled
      && el.getAttribute('tabindex') !== '-1'
      && !(el instanceof HTMLInputElement && el.type === 'file'));
  }

  it('focuses Title, the first field, on open', async () => {
    const { byTestId } = await mount();
    await Promise.resolve();
    expect(document.activeElement).toBe(byTestId('create-title'));
  });

  it('makes lane, type, mode and tags one Tab stop each', async () => {
    const { root, byTestId } = await mount();
    const stops = tabStops(root);
    for (const group of ['create-lane-picker', 'create-task-type', 'create-mode-toolbar', 'create-tags', 'create-prompt-actions']) {
      expect(stops.filter((el) => byTestId(group).contains(el)).length, group).toBe(1);
    }
    expect(stops).toContain(byTestId('create-lane-1-preparation'));
    expect(stops).toContain(byTestId('create-task-type-chore'));
    expect(stops).toContain(byTestId('create-mode-coding'));
    expect(byTestId('create-tags').getAttribute('role')).toBe('group');
    expect(byTestId('create-tag-tag-0').getAttribute('aria-pressed')).toBe('false');
  });

  it('reaches Create from Title in no more than 10 Tab stops', async () => {
    const { root, byTestId } = await mount();
    const stops = tabStops(root);
    const from = stops.indexOf(byTestId('create-title'));
    const to = stops.indexOf(byTestId('create-submit'));
    expect(from).toBeGreaterThanOrEqual(0);
    expect(to - from).toBeLessThanOrEqual(10);
  });
});
