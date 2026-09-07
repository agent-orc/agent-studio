import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectPipelinePanelComponent } from './project-pipeline-panel.component';
import type { PipelineCatalogueStep, PipelineStepSetting } from '../../../task-pipeline';
import type { ProjectPipelineCostTimeline } from '../../../project-token-usage';

/**
 * Nav-rebuild T4a smoke. Compiles + instantiates the standalone component so a
 * broken templateUrl/styleUrl, inject() wiring, or signal init regresses the
 * test rather than only surfacing in a browser. `detectChanges()` is guarded
 * because the projectName effect issues pending test HTTP calls.
 */
describe('ProjectPipelinePanelComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectPipelinePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectPipelinePanelComponent);
    fixture.componentRef.setInput('projectName', undefined);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] ProjectPipelinePanelComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

/**
 * Render check for the configuration grid + prompt-binding cell. Seeds the
 * catalogue / overrides / cost signals directly (the effect's HTTP loads stay
 * pending under provideHttpClientTesting and only set on `next`, so seeded
 * state survives). Asserts: a phase group renders; a configurable aspect step
 * exposes its enable toggle while a core step is locked "always on"; the prompt
 * cell shows the registry template reference plus the legacy inline-override
 * Clear affordance; and LLM-backed steps render their 90-day token sum (no
 * bottom total) while process-only tool steps omit token UI.
 */
describe('ProjectPipelinePanelComponent (render)', () => {
  function catalogue(): PipelineCatalogueStep[] {
    return [
      {
        id: 'core-run', displayName: 'Agent run', kind: 'core', phase: 'core',
        runMode: 'sequential', dependsOn: [], idempotent: false, stub: false,
        usesModel: false, usesPrompt: false, supportsMode: false, cliType: 'claude',
        promptTemplate: null, canDisable: false, defaultEnabled: true, supportsCondition: false,
      },
      {
        id: 'aspect-requirement-fit', displayName: 'Requirement fit', kind: 'aspect', phase: 'aspect',
        runMode: 'parallel', dependsOn: ['core-run'], idempotent: true, stub: false,
        resolvedModel: 'claude-sonnet-4-6', modelSource: 'step', resolvedThinkingLevel: 'medium',
        modelMigration: {
          from: 'claude-sonnet-4-6', to: 'claude-sonnet-5', family: 'claude-sonnet',
          rule: 'latestInFamily:claude-sonnet', catalogVersion: '2026-09-06', safeAuto: true,
          targetAvailable: true, safeAutoCandidate: true, costClassFrom: 'standard', costClassTo: 'standard',
          fromReasoningLevels: ['medium'], toReasoningLevels: ['medium', 'high'], ladderCompatible: true,
          note: 'Compatible upgrade.',
        },
        usesModel: true, supportsEconomyModel: true, usesPrompt: true, supportsMode: true, cliType: 'claude',
        promptTemplate: 'aspect-requirement-fit', canDisable: true, defaultEnabled: true, supportsCondition: true,
      },
      {
        id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', phase: 'aspect',
        runMode: 'parallel', dependsOn: ['core-run'], idempotent: true, stub: false,
        resolvedModel: 'claude-sonnet-4.5', modelSource: 'project',
        usesModel: true, usesPrompt: true, supportsMode: true, cliType: 'claude',
        framework: 'Angular', promptTemplate: 'aspect-code-quality',
        canDisable: true, defaultEnabled: true, supportsCondition: true,
      },
      {
        id: 'post-build-test-gate', displayName: 'Build/test gate', kind: 'tool', phase: 'tool',
        runMode: 'sequential', dependsOn: ['core-run'], idempotent: true, stub: false,
        usesModel: false, usesPrompt: false, supportsMode: true, cliType: null,
        promptTemplate: null, canDisable: true, defaultEnabled: true, supportsCondition: false,
      },
    ];
  }

  function overrides(): Record<string, PipelineStepSetting> {
    return {
      'aspect-requirement-fit': { enabled: true, mode: 'warn', prompt: 'legacy inline text', cliType: 'claude', model: 'claude-sonnet-4-6' },
    };
  }

  function fakeCost(): ProjectPipelineCostTimeline {
    const days = ['2026-06-08', '2026-06-09', '2026-06-10'];
    return {
      project: 'demo', days, windowDays: 90,
      kinds: [
        { kind: 'core', totalTokens: 300_000, totalCostUsd: 0.75, anyModelUnknown: false, cells: [] },
        { kind: 'aspect', totalTokens: 80_000, totalCostUsd: 0.08, anyModelUnknown: true, cells: [] },
      ],
      steps: [
        {
          stepId: 'aspect-requirement-fit', kind: 'aspect', totalTokens: 80_000,
          totalCostUsd: 0.08, anyModelUnknown: true, unpricedRuns: 2,
          pricingGaps: [{ modelId: 'gpt-5.6-sol', reason: 'NoPriceForDate', affectedRuns: 2 }],
        },
        { stepId: 'core-run', kind: 'core', totalTokens: 300_000, totalCostUsd: 0.75, anyModelUnknown: false },
        { stepId: 'post-build-test-gate', kind: 'tool', totalTokens: 10_000, totalCostUsd: 0.01, anyModelUnknown: false },
      ],
      totalTokens: 390_000, totalCostUsd: 0.84, anyModelUnknown: true,
      taskCount: 4, hasData: true, fetchedAt: '2026-06-10T00:00:00Z',
    };
  }

  it('renders groups, prompt bindings, and token sums only for LLM-backed steps', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectPipelinePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectPipelinePanelComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.componentRef.setInput('focusStepId', 'aspect-requirement-fit');
    try { fixture.detectChanges(); } catch { /* pending HTTP, ignore */ }

    fixture.componentInstance.catalogue.set(catalogue());
    fixture.componentInstance.overrides.set(overrides());
    fixture.componentInstance.pipelineCost.set(fakeCost());
    fixture.detectChanges();
    await fixture.whenStable();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-testid="project-detail-pipeline"]')).toBeTruthy();
    const typeSelect = host.querySelector<HTMLSelectElement>('[data-testid="pipeline-type-select"]');
    expect(typeSelect?.value).toBe('task');
    expect(Array.from(typeSelect?.options ?? []).map(option => option.value))
      .toEqual(['task', 'bug', 'feature', 'planning']);
    expect(host.querySelector('[data-testid="pipeline-empty"]')).toBeNull();

    // Phase groups + both step rows.
    expect(host.querySelector('[data-testid="pipeline-group-core"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-group-aspect"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-step-row-core-run"]')).toBeTruthy();
    const aspectRow = host.querySelector('[data-testid="pipeline-step-row-aspect-requirement-fit"]');
    expect(aspectRow).toBeTruthy();
    expect(aspectRow?.getAttribute('aria-current')).toBe('location');
    expect((aspectRow as HTMLDetailsElement | null)?.open).toBe(true);
    expect(aspectRow?.getAttribute('data-kind')).toBe('aspect');
    expect(aspectRow?.textContent).toContain('claude-sonnet-4-6');
    expect(host.querySelector('[data-testid="pipeline-step-info-aspect-requirement-fit"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-step-drag-aspect-requirement-fit"]')?.getAttribute('draggable')).toBe('true');
    expect(host.querySelector('[data-testid="pipeline-step-prompt-open-aspect-requirement-fit"]')?.textContent).toContain('Prompt');
    expect(aspectRow?.textContent).toContain('Label');
    expect(aspectRow?.textContent).toContain('Setting');
    expect(aspectRow?.textContent).toContain('Explanation');
    expect(host.querySelector('[data-testid="pipeline-step-setting-run-aspect-requirement-fit"]')?.textContent).toContain('parallel after core-run');
    expect(host.querySelector('[data-testid="pipeline-step-setting-active-aspect-requirement-fit"]')).toBeNull();
    expect(host.querySelector('[data-testid="pipeline-step-setting-model-aspect-requirement-fit"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-step-setting-economy-aspect-requirement-fit"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-step-setting-prompt-aspect-requirement-fit"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-step-setting-gate-aspect-requirement-fit"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-step-setting-condition-aspect-requirement-fit"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-step-setting-retry-aspect-requirement-fit"]')?.textContent).toContain('Safe to retry');
    expect(aspectRow?.textContent).not.toContain('Run:');
    expect(aspectRow?.textContent).not.toContain('Model:');
    expect(aspectRow?.textContent).not.toContain('Prompt:');
    expect(aspectRow?.textContent).not.toContain('Gate:');
    expect(aspectRow?.textContent).not.toContain('When:');

    // On/Off lives directly in the collapsed row; fixed core stays labelled.
    const coreToggle = host.querySelector<HTMLInputElement>('[data-testid="pipeline-step-row-enabled-core-run"]');
    const aspectToggle = host.querySelector<HTMLInputElement>('[data-testid="pipeline-step-row-enabled-aspect-requirement-fit"]');
    expect(coreToggle).toBeNull();
    expect(host.querySelector('[data-testid="pipeline-step-row-core-run"]')?.textContent)
      .toContain('always on');
    expect(aspectToggle?.disabled).toBe(false);
    expect(aspectToggle?.closest('summary')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-step-framework-aspect-code-quality"]')?.textContent)
      .toContain('Angular');

    // Prompt binding cell: registry template reference + legacy inline override + Clear.
    const promptCell = host.querySelector('[data-testid="pipeline-step-prompt-aspect-requirement-fit"]');
    expect(promptCell?.textContent).toContain('aspect-requirement-fit');
    expect(host.querySelector('[data-testid="pipeline-step-prompt-clear-aspect-requirement-fit"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-step-prompt-manage-aspect-requirement-fit"]')).toBeTruthy();

    // Per-step token sum: the aspect step shows its 90-day token total with the
    // unknown-price star; the core step shows its own sum. Tool steps show no
    // token UI even if a malformed cost payload contains a tool entry.
    const aspectTokens = host.querySelector('[data-testid="pipeline-step-tokens-aspect-requirement-fit"]');
    expect(aspectTokens?.textContent).toContain('80.0k');
    expect(aspectTokens?.textContent).toContain('*');
    expect(host.querySelector('[data-testid="pipeline-step-tokens-core-run"]')?.textContent).toContain('300.0k');
    expect(fixture.componentInstance.stepTokenTooltip(fixture.componentInstance.rows().find(r => r.id === 'core-run')!))
      .toContain('Estimated cost: $0.75');
    expect(fixture.componentInstance.stepTokenTooltip(fixture.componentInstance.rows().find(r => r.id === 'core-run')!))
      .toContain('historical list prices');
    expect(fixture.componentInstance.stepTokenTooltip(fixture.componentInstance.rows().find(r => r.id === 'aspect-code-quality')!))
      .toContain('Estimated cost: no price data');
    const mixedTooltip = fixture.componentInstance.stepTokenTooltip(
      fixture.componentInstance.rows().find(r => r.id === 'aspect-requirement-fit')!,
    );
    expect(mixedTooltip).toContain('$0.08 (partial');
    expect(mixedTooltip).toContain('gpt-5.6-sol');
    expect(mixedTooltip).toContain('NoPriceForDate');
    expect(host.querySelector('[data-testid="pipeline-step-setting-tokens-aspect-requirement-fit"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="pipeline-step-tokens-post-build-test-gate"]')).toBeNull();
    expect(host.querySelector('[data-testid="pipeline-step-setting-tokens-post-build-test-gate"]')).toBeNull();
    expect(host.querySelector('[data-testid="pipeline-cost"]')).toBeNull();
    expect(host.querySelector('[data-testid="pipeline-cost-total"]')).toBeNull();

    // Fixed-width type badges use one three-character vocabulary.
    expect(host.querySelector('[data-testid="pipeline-step-kind-core-run"]')?.textContent?.trim()).toBe('COR');
    expect(host.querySelector('[data-testid="pipeline-step-kind-aspect-requirement-fit"]')?.textContent?.trim()).toBe('ASP');
    expect(host.querySelector('[data-testid="pipeline-step-kind-post-build-test-gate"]')?.textContent?.trim()).toBe('TOO');

    const apply = host.querySelector<HTMLButtonElement>(
      '[data-testid="pipeline-step-model-migration-aspect-requirement-fit-apply"]',
    );
    expect(apply).toBeTruthy();
    apply?.click();
    const write = TestBed.inject(HttpTestingController).expectOne('/api/projects/demo/pipeline-step');
    expect(write.request.body.model).toBe('claude-sonnet-5');
    write.flush({ pipelineSteps: {
      'aspect-requirement-fit': { enabled: true, cliType: 'claude', model: 'claude-sonnet-5' },
    } });
    fixture.detectChanges();
    expect(fixture.componentInstance.rows().find(row => row.id === 'aspect-requirement-fit')?.modelMigration).toBeNull();
    expect(host.querySelector('[data-testid="pipeline-step-model-migration-aspect-requirement-fit"]')).toBeNull();
  });

  it('emits openPrompts from the summary prompt chip and Manage in Prompts', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectPipelinePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectPipelinePanelComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    try { fixture.detectChanges(); } catch { /* pending HTTP, ignore */ }

    fixture.componentInstance.catalogue.set(catalogue());
    fixture.componentInstance.overrides.set(overrides());
    fixture.detectChanges();

    let opened = 0;
    fixture.componentInstance.openPrompts.subscribe(() => opened++);

    const summaryPrompt = host(fixture).querySelector<HTMLButtonElement>(
      '[data-testid="pipeline-step-prompt-open-aspect-requirement-fit"]',
    );
    summaryPrompt?.click();
    expect(opened).toBe(1);

    const manage = host(fixture).querySelector<HTMLButtonElement>(
      '[data-testid="pipeline-step-prompt-manage-aspect-requirement-fit"]',
    );
    manage?.click();
    expect(opened).toBe(2);
  });
});

function host(fixture: { nativeElement: HTMLElement }): HTMLElement {
  return fixture.nativeElement;
}
