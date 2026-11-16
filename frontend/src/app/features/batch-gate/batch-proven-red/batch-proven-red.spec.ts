import { TestBed } from '@angular/core/testing';
import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { isolatedRedState } from '../testing/batch-fixtures';
import { BatchProvenRedComponent, shortEvidenceName } from './batch-proven-red';

describe('BatchProvenRedComponent', () => {
  const culprit = isolatedRedState.isolationFindings!.find(finding => finding.status === 'proven-red')!;

  async function render(finding = culprit) {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(BatchProvenRedComponent);
    fixture.componentRef.setInput('project', 'Demo');
    fixture.componentRef.setInput('finding', finding);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('lists the failing tests with their excerpts and links the evidence', async () => {
    const host = await render();
    expect(host.querySelectorAll('[data-testid="batch-proven-red-test"]')).toHaveLength(2);
    expect(host.querySelectorAll('[data-testid="batch-proven-red-excerpt"]')).toHaveLength(1);
    expect(host.querySelector('[data-testid="batch-proven-red-evidence"]')!.getAttribute('href'))
      .toBe('/api/projects/Demo/batch-gate/isolation-evidence?parentBatchId=batch-14&taskKey=AGT-2');
  });

  it('shows the evidence folder and file name and the full path only on demand', async () => {
    const host = await render();
    expect(host.querySelector('[data-testid="batch-proven-red-evidence-name"]')!.textContent).toBe('batch-17/gate-evidence.jsonl');
    expect(host.querySelector('[data-testid="batch-proven-red-evidence-path"]')).toBeNull();
    const toggle = host.querySelector<HTMLButtonElement>('[data-testid="batch-proven-red-path-toggle"]')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    toggle.click();
    await TestBed.inject(ApplicationRef).whenStable();
    expect(host.querySelector('[data-testid="batch-proven-red-evidence-path"]')!.textContent).toBe(culprit.evidencePath);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(toggle.textContent!.trim()).toBe('Hide full path');
  });

  it('shortens Windows and bare evidence paths', () => {
    expect(shortEvidenceName('C:\\gates\\batch-3\\gate-evidence.jsonl')).toBe('batch-3/gate-evidence.jsonl');
    expect(shortEvidenceName('gate-evidence.jsonl')).toBe('gate-evidence.jsonl');
  });

  it('says so when the run recorded no test names or its evidence is gone', async () => {
    const host = await render({ ...culprit, failures: undefined, evidenceAvailable: false });
    expect(host.textContent).toContain('No failing test names were recorded for the run that proved it.');
    expect(host.querySelector('[data-testid="batch-proven-red-evidence"]')).toBeNull();
    expect(host.textContent).toContain('Evidence file unavailable');
  });
});
