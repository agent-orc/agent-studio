import { afterEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { OverviewStepTokenModalComponent } from './overview-step-token-modal.component';
import type { PipelineRowVm } from '../pipeline-row.vm';

/**
 * AGT-2819 split the per-step token breakdown out of
 * `overview-pane.component.ts`. The pane's own DOM assertions for this dialog
 * still run through the pane; these cases pin the extracted component's public
 * behaviour directly.
 */
function row(overrides: Partial<PipelineRowVm> = {}): PipelineRowVm {
  return {
    id: 'core-agent-run',
    label: 'Agent execution',
    kind: 'core',
    phaseKey: 'core',
    phaseLabel: 'Agent',
    phaseDescription: 'The agent run.',
    startsPhase: true,
    runMode: 'sequential',
    isFinalVerdict: false,
    historical: false,
    enabled: true,
    canDisable: false,
    hasExecution: true,
    config: null,
    status: 'passed',
    statusTooltip: null,
    skipHint: null,
    remoteNotApplicable: false,
    attentionRequired: false,
    remoteReviewDetail: null,
    model: 'claude-opus-4-8',
    thinkingLevel: null,
    cliType: null,
    modelIsResolved: false,
    modelTooltip: null,
    modelEditable: false,
    modelOverride: '',
    thinkingLevelOverride: null,
    verdict: null,
    concernTooltip: null,
    explanation: { title: 'Agent execution', body: 'The agent run.' },
    durationMs: 12_000,
    startedAt: '2026-09-14T08:00:00Z',
    completedAt: '2026-09-14T08:00:12Z',
    tokenUsageSource: null,
    inputTokens: 100,
    outputTokens: 200,
    cacheReadTokens: 300,
    cacheCreationTokens: 400,
    totalTokens: 1000,
    inputCostUsd: 0.01,
    outputCostUsd: 0.02,
    cacheReadCostUsd: 0.03,
    cacheCreationCostUsd: 0.04,
    costUsd: 0.1,
    costKnown: true,
    unpricedRuns: 0,
    pricingGaps: [],
    tokenTooltip: null,
    costTooltip: null,
    ...overrides,
  };
}

async function build(vm: PipelineRowVm, inputs: Record<string, unknown> = {}) {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [OverviewStepTokenModalComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(OverviewStepTokenModalComponent);
  fixture.componentRef.setInput('row', vm);
  for (const [name, value] of Object.entries(inputs)) {
    fixture.componentRef.setInput(name, value);
  }
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

afterEach(() => {
  TestBed.resetTestingModule();
});

describe('OverviewStepTokenModalComponent', () => {
  it('renders the four usage components plus a total row', async () => {
    const fixture = await build(row());
    const table = document.body.querySelector('[data-testid="overview-step-token-modal-breakdown"]');

    const labels = [...(table?.querySelectorAll('tbody tr td:first-child') ?? [])]
      .map(cell => cell.textContent?.trim());
    expect(labels).toEqual(['Input', 'Output', 'Cache read', 'Cache write', 'Total']);
    fixture.destroy();
  });

  it('breakdown rows carry each usage component with its own cost', async () => {
    const fixture = await build(row());
    const parts = fixture.componentInstance.tokenBreakdownRows(row());

    expect(parts.map(part => part.label)).toEqual(['Input', 'Output', 'Cache read', 'Cache write']);
    expect(parts.map(part => part.tokens)).toEqual([100, 200, 300, 400]);
    expect(parts.map(part => part.costUsd)).toEqual([0.01, 0.02, 0.03, 0.04]);
    fixture.destroy();
  });

  it('notes an honest mismatch when the components do not sum to the reported total', async () => {
    const fixture = await build(row({ totalTokens: 999 }));

    expect(fixture.componentInstance.tokenComponentTotal(row())).toBe(1000);
    expect(fixture.componentInstance.tokenComponentMatchesTotal(row({ totalTokens: 999 }))).toBe(false);
    expect(
      document.body.querySelector('[data-testid="overview-step-token-modal-total-note"]'),
    ).not.toBeNull();
    fixture.destroy();
  });

  it('no mismatch note when the components sum to the reported total', async () => {
    const fixture = await build(row());

    expect(fixture.componentInstance.tokenComponentMatchesTotal(row())).toBe(true);
    expect(
      document.body.querySelector('[data-testid="overview-step-token-modal-total-note"]'),
    ).toBeNull();
    fixture.destroy();
  });

  it('call count reads the agent run count only for the CORE row of the current run', async () => {
    const fixture = await build(row(), { agentRunCount: 3, currentRun: true });
    const c = fixture.componentInstance;

    expect(c.tokenStepCallsLabel(row())).toBe('3 agent runs');
    fixture.componentRef.setInput('currentRun', false);
    expect(c.tokenStepCallsLabel(row())).toBe('1 step execution');
    expect(c.tokenStepCallsLabel(row({ kind: 'aspect' }))).toBe('1 step execution');
    expect(c.tokenStepCallsLabel(row({ kind: 'aspect', status: 'planned' }))).toBe('Not reported');
    fixture.destroy();
  });

  it('source label prefers the recorded usage source, then falls back per kind', async () => {
    const fixture = await build(row());
    const c = fixture.componentInstance;

    expect(c.tokenStepSourceLabel(row({ tokenUsageSource: '  cli-footer  ' }))).toBe('cli-footer');
    expect(c.tokenStepSourceLabel(row({ tokenUsageSource: null }))).toBe('CORE agent run');
    expect(c.tokenStepSourceLabel(row({ kind: 'aspect', tokenUsageSource: null })))
      .toBe('Pipeline step usage');
    fixture.destroy();
  });

  it('time label is honest when nothing was recorded', async () => {
    const fixture = await build(row());
    const c = fixture.componentInstance;

    expect(c.tokenStepTimeLabel(row({ startedAt: null, completedAt: null, durationMs: 0 })))
      .toBe('No step time recorded');
    expect(c.tokenStepTimeLabel(row())).toContain('Duration');
    fixture.destroy();
  });

  it('a partial cost is only flagged when some runs stayed unpriced', async () => {
    const fixture = await build(row());
    const c = fixture.componentInstance;

    expect(c.isPartialCost(0.1, 2)).toBe(true);
    expect(c.isPartialCost(0.1, 0)).toBe(false);
    expect(c.isPartialCost(0, 2)).toBe(false);
    fixture.destroy();
  });

  it('asking the dialog to close emits closeRequest instead of self-closing', async () => {
    const fixture = await build(row());
    let closed = 0;
    fixture.componentInstance.closeRequest.subscribe(() => closed++);

    const dialogClose = document.body.querySelector<HTMLElement>(
      '[data-testid="overview-step-token-modal"] [aria-label="Close"], '
      + '[data-testid="overview-step-token-modal"] button',
    );
    dialogClose?.click();

    expect(closed).toBeGreaterThan(0);
    fixture.destroy();
  });
});
