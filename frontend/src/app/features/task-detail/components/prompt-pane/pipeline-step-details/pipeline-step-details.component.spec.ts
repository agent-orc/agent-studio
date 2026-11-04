import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TaskService } from '../../../../../services/task.service';
import type { PipelineStepResultHeader } from '../pipeline-step-result/pipeline-step-result.component';
import { PipelineStepDetailsComponent } from './pipeline-step-details.component';

const RESULT_HEADER: PipelineStepResultHeader = {
  label: 'Code quality',
  statusTone: 'success',
  statusLabel: 'Passed',
  status: 'passed',
  verdict: 'pass',
  model: 'claude-haiku-4-5',
  durationLabel: '12s',
  tokensLabel: '4.2k',
  costLabel: '$0.01',
};

interface DetailInputs {
  promptMarkdown: string;
  resultFile: string | null;
  resultHeader: PipelineStepResultHeader | null;
  concernTitle: string | null;
  concernBody: string | null;
}

function mount(overrides: Partial<DetailInputs> = {}) {
  const fixture = TestBed.createComponent(PipelineStepDetailsComponent);
  const inputs: DetailInputs = {
    promptMarkdown: '',
    resultFile: null,
    resultHeader: null,
    concernTitle: null,
    concernBody: null,
    ...overrides,
  };

  fixture.componentRef.setInput('stepId', 'aspect-code-quality');
  fixture.componentRef.setInput('label', 'Code quality');
  fixture.componentRef.setInput('docs', 'Checks maintainability and correctness.');
  fixture.componentRef.setInput('jobId', 'job-1');
  for (const [name, value] of Object.entries(inputs)) {
    fixture.componentRef.setInput(name, value);
  }
  fixture.detectChanges();
  return fixture;
}

function openDetails(fixture: ReturnType<typeof mount>): HTMLElement {
  const trigger = fixture.nativeElement.querySelector(
    'button[aria-label="Open details for Code quality"]',
  ) as HTMLButtonElement | null;
  expect(trigger).not.toBeNull();
  trigger?.click();
  fixture.detectChanges();

  const dialog = document.body.querySelector(
    '[data-testid="overview-pipeline-step-details-dialog"]',
  ) as HTMLElement | null;
  expect(dialog).not.toBeNull();
  return dialog as HTMLElement;
}

describe('PipelineStepDetailsComponent', () => {
  const readJobFile = vi.fn().mockReturnValue(of('verified result'));

  beforeEach(() => {
    readJobFile.mockClear();
    TestBed.configureTestingModule({
      imports: [PipelineStepDetailsComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: { readJobFile } },
      ],
    });
  });

  afterEach(() => {
    document.body.querySelector('.studio-overlay-root')?.remove();
  });

  it('opens its dialog from the accessible trigger and always shows Docs', () => {
    const fixture = mount();
    const trigger = fixture.nativeElement.querySelector(
      'button[aria-label="Open details for Code quality"]',
    ) as HTMLButtonElement;

    expect(trigger.getAttribute('aria-haspopup')).toBe('dialog');
    expect(trigger.getAttribute('aria-expanded')).toBe('false');

    const dialog = openDetails(fixture);

    expect(trigger.getAttribute('aria-expanded')).toBe('true');
    expect(dialog.getAttribute('role')).toBe('dialog');
    expect(dialog.getAttribute('aria-label')).toBe('Code quality');
    expect(
      dialog.querySelector('[data-testid="overview-pipeline-step-docs"]')?.textContent,
    ).toContain('Checks maintainability and correctness.');
  });

  it('shows Prompt only when prompt markdown is non-blank', () => {
    const fixture = mount({ promptMarkdown: '   ' });
    const dialog = openDetails(fixture);

    expect(dialog.querySelector('[data-testid="overview-pipeline-step-prompt-detail"]')).toBeNull();

    fixture.componentRef.setInput('promptMarkdown', 'Review the implementation.');
    fixture.detectChanges();

    expect(
      dialog.querySelector('[data-testid="overview-pipeline-step-prompt-detail"]'),
    ).not.toBeNull();
    expect(
      dialog.querySelector('[data-testid="overview-pipeline-step-prompt-aspect-code-quality"]'),
    ).not.toBeNull();
  });

  it('shows Concerns only when a concern body exists', () => {
    const fixture = mount({ concernTitle: 'Review concern' });
    const dialog = openDetails(fixture);

    expect(
      dialog.querySelector('[data-testid="overview-pipeline-step-concerns-detail"]'),
    ).toBeNull();

    fixture.componentRef.setInput('concernBody', 'A regression remains possible.');
    fixture.detectChanges();

    const concerns = dialog.querySelector('[data-testid="overview-pipeline-step-concerns-detail"]');
    expect(concerns?.textContent).toContain('Review concern');
    expect(concerns?.querySelector('[data-testid="overview-pipeline-step-concerns-state"]')?.getAttribute('data-tone')).toBe('warning');
    expect(concerns?.textContent).toContain('A regression remains possible.');
  });

  it('shows Result only when both the verified result file and header exist', () => {
    const fixture = mount();
    const dialog = openDetails(fixture);
    const result = () =>
      dialog.querySelector('[data-testid="overview-pipeline-step-result-detail"]');

    expect(result()).toBeNull();

    fixture.componentRef.setInput('resultHeader', RESULT_HEADER);
    fixture.detectChanges();
    expect(result()).toBeNull();

    fixture.componentRef.setInput('resultHeader', null);
    fixture.componentRef.setInput('resultFile', 'aspect-code-quality.md');
    fixture.detectChanges();
    expect(result()).toBeNull();

    fixture.componentRef.setInput('resultHeader', RESULT_HEADER);
    fixture.detectChanges();

    expect(result()).not.toBeNull();
    expect(result()?.querySelector('app-pipeline-step-result')).not.toBeNull();
    expect(readJobFile).not.toHaveBeenCalled();
  });
});
