import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of, throwError } from 'rxjs';
import {
  PipelineStepResultComponent,
  type PipelineStepResultHeader,
} from './pipeline-step-result.component';
import { TaskService } from '../../../../../services/task.service';

const HEADER: PipelineStepResultHeader = {
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

const ASPECT_MD = [
  '---',
  'aspect: code-quality',
  'status: pass',
  '---',
  '',
  '## Model reply',
  '',
  '```',
  '## Code Quality Review',
  '',
  'No production code changes.',
  '```',
  '[[ASPECT_VERDICT: status=pass]]',
  '```',
  '',
  '[[TASK_DONE]]',
  '```',
].join('\n');

function setup(text: string | (() => never) = ASPECT_MD) {
  const readJobFile = vi.fn().mockReturnValue(
    typeof text === 'function' ? throwError(() => new Error('boom')) : of(text),
  );
  TestBed.configureTestingModule({
    imports: [PipelineStepResultComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: { readJobFile } },
    ],
  });
  const fixture = TestBed.createComponent(PipelineStepResultComponent);
  fixture.componentRef.setInput('header', HEADER);
  fixture.componentRef.setInput('jobId', 'job-1');
  fixture.componentRef.setInput('fileName', 'aspect-code-quality.md');
  fixture.detectChanges();
  return { fixture, readJobFile };
}

const root = (fixture: { nativeElement: HTMLElement }) => fixture.nativeElement as HTMLElement;

describe('PipelineStepResultComponent', () => {
  it('is collapsed by default and does not fetch', () => {
    const { fixture, readJobFile } = setup();
    expect(readJobFile).not.toHaveBeenCalled();
    expect(root(fixture).querySelector('[data-testid="pipeline-step-result-card"]')).toBeNull();
  });

  it('lazy-loads on first expand and renders the cleaned markdown', async () => {
    const { fixture, readJobFile } = setup();
    fixture.componentInstance.toggle();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(readJobFile).toHaveBeenCalledTimes(1);
    const body = root(fixture).querySelector('[data-testid="pipeline-step-result-body"]');
    expect(body?.textContent).toContain('Code Quality Review');
    expect(body?.textContent).toContain('No production code changes.');
    // Machine sentinels never reach the rendered card.
    expect(body?.textContent).not.toContain('ASPECT_VERDICT');
    expect(body?.textContent).not.toContain('TASK_DONE');
  });

  it('renders the step result through the shared file-source-history mechanic', async () => {
    const { fixture } = setup();
    fixture.componentInstance.toggle();
    await fixture.whenStable();
    fixture.detectChanges();

    // The body is now backed by app-file-source-history, so the file/history
    // toggle is available on every pipeline step - the same mechanic the Files
    // and Code-Review panes use.
    const body = root(fixture).querySelector('[data-testid="pipeline-step-result-body"]');
    expect(body?.querySelector('[data-testid="file-source-history"]')).not.toBeNull();
    expect(body?.querySelector('[data-testid="file-source-history-toggle"]')).not.toBeNull();
    expect(body?.textContent).toContain('aspect-code-quality.md');
  });

  it('renders the header title, status and verdict chip when open', async () => {
    const { fixture } = setup();
    fixture.componentInstance.toggle();
    await fixture.whenStable();
    fixture.detectChanges();

    const card = root(fixture).querySelector('[data-testid="pipeline-step-result-card"]');
    expect(card?.textContent).toContain('Code quality');
    expect(card?.textContent).toContain('Passed');
    expect(card?.classList.contains('step-result__popover')).toBe(true);
    expect(
      root(fixture).querySelector('[data-testid="pipeline-step-result-verdict"]')?.textContent,
    ).toContain('pass');
    expect(root(fixture).querySelector('[data-testid="pipeline-step-result-verdict"]')?.getAttribute('data-tone'))
      .toBe('success');
    expect(root(fixture).querySelector('[data-testid="pipeline-step-result-status"]')?.getAttribute('data-tone'))
      .toBe('success');
  });

  it('does not refetch when toggled closed then open again', async () => {
    const { fixture, readJobFile } = setup();
    fixture.componentInstance.toggle();
    await fixture.whenStable();
    fixture.componentInstance.toggle(); // close
    fixture.componentInstance.toggle(); // open again
    await fixture.whenStable();
    expect(readJobFile).toHaveBeenCalledTimes(1);
  });

  it('shows an error note when the fetch fails', async () => {
    const { fixture } = setup(() => {
      throw new Error('unused');
    });
    fixture.componentInstance.toggle();
    await fixture.whenStable();
    fixture.detectChanges();
    const state = root(fixture).querySelector('[data-testid="pipeline-step-result-error"]');
    expect(state?.getAttribute('data-tone')).toBe('danger');
  });
});
