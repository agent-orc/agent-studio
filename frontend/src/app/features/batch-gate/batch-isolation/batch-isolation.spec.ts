import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { batchIsolationRows } from '../../../models/batch-gate.model';
import { isolatedRedState } from '../testing/batch-fixtures';
import { BatchIsolationComponent } from './batch-isolation';

describe('BatchIsolationComponent', () => {
  it('renders one tone per member status and the failing tests only for the proven-red member', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(BatchIsolationComponent);
    const findings = isolatedRedState.isolationFindings!.filter(finding => finding.taskKey !== 'AGT-4');
    const batch = isolatedRedState.batches.find(item => item.batchId === 'batch-14')!;
    fixture.componentRef.setInput('project', 'Demo');
    fixture.componentRef.setInput('batchId', 'batch-14');
    fixture.componentRef.setInput('rows', batchIsolationRows({ ...isolatedRedState, isolationFindings: findings }, batch));
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const tones = [...host.querySelectorAll<HTMLElement>('[data-testid="batch-isolation-status"]')]
      .map(status => [status.dataset['tone'], status.textContent!.trim()]);
    expect(tones).toEqual([['success', 'Proven green'], ['danger', 'Proven red'], ['success', 'Proven green'], ['neutral', 'Not yet isolated']]);
    expect(host.querySelectorAll('app-batch-proven-red')).toHaveLength(1);
    expect(host.querySelector('[data-task-key="AGT-2"] app-batch-proven-red')).not.toBeNull();
    expect(host.querySelector('[data-task-key="AGT-4"]')?.textContent).toContain('Waiting for: An isolation verdict');
  });
});
