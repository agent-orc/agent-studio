import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import type { PipelineAdminRow } from '../pipeline-config.util';
import { PipelineStepExecutionComponent } from './pipeline-step-execution.component';

describe('PipelineStepExecutionComponent', () => {
  it('shows the resolved command and renders probe output', async () => {
    await TestBed.configureTestingModule({
      imports: [PipelineStepExecutionComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PipelineStepExecutionComponent);
    fixture.componentRef.setInput('projectName', 'Angular App');
    fixture.componentRef.setInput('step', step({ applicable: true }));
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-testid="pipeline-step-commands-post-lint-scss"]')?.textContent)
      .toContain('cd frontend && npx stylelint "src/**/*.scss"');

    host.querySelector<HTMLButtonElement>('[data-testid="pipeline-step-probe-post-lint-scss"]')?.click();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/projects/Angular%20App/pipeline-steps/post-lint-scss/probe')
      .flush({
        stepId: 'post-lint-scss', status: 'passed', applicable: true,
        exitCode: 0, durationMs: 42, output: 'stylelint passed', queueWaitMs: 7,
      });
    fixture.detectChanges();

    const output = host.querySelector('[data-testid="pipeline-step-probe-output-post-lint-scss"]');
    expect(output?.getAttribute('data-status')).toBe('passed');
    expect(output?.textContent).toContain('stylelint passed');
    expect(output?.textContent).toContain('lock wait 7 ms');
  });

  it('keeps an inapplicable framework-specific step visible', async () => {
    await TestBed.configureTestingModule({
      imports: [PipelineStepExecutionComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PipelineStepExecutionComponent);
    fixture.componentRef.setInput('projectName', 'DotNet App');
    fixture.componentRef.setInput('step', step({ applicable: false }));
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="pipeline-step-not-applicable-post-lint-scss"]')?.textContent)
      .toContain('Requires angular');
  });
});

function step(overrides: Partial<PipelineAdminRow>): PipelineAdminRow {
  return {
    id: 'post-lint-scss', displayName: 'Frontend stylelint', kind: 'tool', appliesTo: 'angular',
    applicable: true, effectiveExecution: {
      executionKind: 'shell', source: 'catalogue',
      commands: [{ workingSubdir: 'frontend', command: 'npx stylelint "src/**/*.scss"' }],
    },
    runMode: 'sequential', dependsOn: [], idempotent: true, stub: false, deferred: false,
    usesModel: false, supportsEconomyModel: false, usesPrompt: false, supportsMode: true,
    canDisable: true, supportsCondition: true, phase: 'tool', enabled: true, economyModel: false,
    cliType: '', model: '', thinkingLevel: '', effectiveCliType: '', effectiveModel: '',
    effectiveModelSource: '', effectiveThinkingLevel: '', activeCliType: '', activeModel: '',
    activeThinkingLevel: '', quotaFallback: false, quotaOutcome: 'LaunchPrimary', quotaReason: '',
    quotaActivatedAt: '', quotaResetAt: '', prompt: '', promptTemplate: '', mode: '',
    condition: '', conditionValue: '', conditionNeedsValue: false, canMoveUp: false, canMoveDown: false,
    tokenSum: null, tokenUnknown: false, ...overrides,
    tokenCostUsd: overrides.tokenCostUsd ?? null,
  };
}
