import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import type { BatchQuarantinedRecord } from '../../../models/batch-gate.model';
import { BatchQuarantineComponent } from './batch-quarantine';

const record = (kind: string, batchId: string, project: string | null): BatchQuarantinedRecord => ({
  batchId, project, kind, file: `/state/batch-gates/${batchId}/${kind}.json`,
  error: "'s' is an invalid start of a value.", detectedAtUtc: '2026-11-10T09:00:00Z',
  nextStep: `Restore this file from a backup. Until then the pilot skips batch ${batchId}.`,
});

describe('BatchQuarantineComponent', () => {
  it('names each record with its batch, project, file, parse error and next step', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(BatchQuarantineComponent);
    fixture.componentRef.setInput('records', [record('manifest', 'batch-1', 'alpha'), record('run', 'batch-2', null)]);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const indicator = host.querySelector<HTMLElement>('[data-testid="status-indicator"]')!;
    expect(indicator.dataset['tone']).toBe('warning');
    expect(indicator.textContent).toContain('2 batch records unreadable');
    const rows = [...host.querySelectorAll<HTMLElement>('[data-testid="batch-quarantine-row"]')];
    expect(rows.map(row => row.dataset['kind'])).toEqual(['manifest', 'run']);
    expect(rows[0].textContent).toContain('Manifest of batch batch-1');
    expect(rows[0].textContent).toContain('project alpha');
    expect(rows[0].textContent).toContain('/state/batch-gates/batch-1/manifest.json');
    expect(rows[0].textContent).toContain("'s' is an invalid start of a value.");
    expect(rows[0].textContent).toContain('Until then the pilot skips batch batch-1.');
    expect(rows[1].textContent).toContain('Run record of batch batch-2');
    expect(rows[1].textContent).toContain('project unknown');
  });

  it('uses the singular title for one record', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(BatchQuarantineComponent);
    fixture.componentRef.setInput('records', [record('state', 'batch-3', 'alpha')]);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Batch record unreadable');
  });
});
