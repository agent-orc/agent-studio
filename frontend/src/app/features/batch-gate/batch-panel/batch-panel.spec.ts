import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi } from 'vitest';
import { BatchPanelComponent } from './batch-panel';
import { redHalvingState } from '../testing/batch-fixtures';
import { ModalStackService } from '../../../services/modal-stack.service';

describe('BatchPanelComponent', () => {
  it('shows halving, advisor veto and fallback membership from the state contract', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const fixture: ComponentFixture<BatchPanelComponent> = TestBed.createComponent(BatchPanelComponent);
    fixture.componentRef.setInput('project', 'Demo');
    fixture.componentRef.setInput('closed', vi.fn());
    await fixture.whenStable();
    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('[aria-label="Close batch panel"]'));
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/batch-gate/state?history=10').flush(redHalvingState);
    http.expectOne('/api/projects/Demo/batch-gate/failed-tests?windowDays=7').flush({ project: 'Demo', windowDays: 7, tests: [] });
    await Promise.resolve();
    fixture.detectChanges();
    await fixture.whenStable();
    const text = fixture.nativeElement.textContent as string;
    expect(fixture.nativeElement.querySelector('[data-testid="batch-advisor"]').textContent).toContain('veto');
    expect(text).toContain('from batch-7');
    expect(text).toContain('red');
    expect(text).toContain('test-failure');
    expect(text).toContain('AGT-3 · overlap separate: src/shared.cs');
    expect(text).toContain('new batch: overlap separate from waiting batch batch-7, files src/shared.cs');
    expect(text).toContain('agent-runner-01');
    expect(fixture.nativeElement.querySelector('[data-testid="batch-gate-slot-usage"]').textContent)
      .toContain('2/3 busy');
    expect((fixture.nativeElement.querySelector('[data-testid="batch-gate-capacity"]').textContent as string).replace(/\s+/g, ' '))
      .toContain('3 slots · 6 processors per gate · 24 host cores');
    const key = new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true });
    fixture.nativeElement.querySelector('.batch-panel__body').dispatchEvent(key);
    expect(key.defaultPrevented).toBe(true);
    http.verify();
  });

  it('requires page confirmation before ejecting a waiting card', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(BatchPanelComponent);
    fixture.componentRef.setInput('project', 'Demo');
    fixture.componentRef.setInput('closed', vi.fn());
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/batch-gate/state?history=10').flush(redHalvingState);
    http.expectOne('/api/projects/Demo/batch-gate/failed-tests?windowDays=7').flush({ project: 'Demo', windowDays: 7, tests: [] });
    await Promise.resolve();
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[aria-label="Eject AGT-1"]').click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[data-testid="batch-confirm"]')).toBeTruthy();
    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('[data-testid="batch-confirm"] button'));
    expect(http.match('/api/projects/Demo/batch-gate/members/AGT-1/eject')).toHaveLength(0);
    fixture.nativeElement.querySelector('[data-testid="batch-confirm"] button').click();
    http.expectOne('/api/projects/Demo/batch-gate/members/AGT-1/eject').flush({}, { status: 202, statusText: 'Accepted' });
    http.expectOne('/api/projects/Demo/batch-gate/state?history=10').flush(redHalvingState);
    await fixture.whenStable();
    http.verify();
  });

  it('confirms close-now and owns Escape above the task detail', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const lower = vi.fn();
    const stack = TestBed.inject(ModalStackService);
    const dispose = stack.push('task-detail', lower);
    const closed = vi.fn();
    const fixture = TestBed.createComponent(BatchPanelComponent);
    fixture.componentRef.setInput('project', 'Demo');
    fixture.componentRef.setInput('closed', closed);
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/batch-gate/state?history=10').flush(redHalvingState);
    http.expectOne('/api/projects/Demo/batch-gate/failed-tests?windowDays=7').flush({ project: 'Demo', windowDays: 7, tests: [] });
    await Promise.resolve();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.nativeElement.querySelector('[data-testid="batch-close-now"]').click();
    fixture.detectChanges();
    expect(http.match('/api/projects/Demo/batch-gate/close-now')).toHaveLength(0);
    fixture.nativeElement.querySelector('[data-testid="batch-confirm"] button').click();
    http.expectOne('/api/projects/Demo/batch-gate/close-now').flush({}, { status: 202, statusText: 'Accepted' });
    http.expectOne('/api/projects/Demo/batch-gate/state?history=10').flush(redHalvingState);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(closed).toHaveBeenCalledOnce();
    expect(lower).not.toHaveBeenCalled();
    fixture.destroy();
    dispose();
    http.verify();
  });

  it('ranks a flaky suspect and opens a prefilled bug card for operator confirmation', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(BatchPanelComponent);
    fixture.componentRef.setInput('project', 'Demo');
    fixture.componentRef.setInput('closed', vi.fn());
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/batch-gate/state?history=10').flush(redHalvingState);
    http.expectOne('/api/projects/Demo/batch-gate/failed-tests?windowDays=7').flush({ project: 'Demo', windowDays: 7, tests: [{
      testName: 'Product.Tests.Unstable', batches: 3, runs: 4, distinctMemberSets: 3,
      firstFailureUtc: '2026-11-01T12:00:00Z', lastFailureUtc: '2026-11-05T12:00:00Z',
      latestErrorExcerpt: 'Error Message: timed out', flakyRerunPassed: true,
      flakySuspect: true, batchIds: ['one', 'two', 'three'], failures: [{
        testName: 'Product.Tests.Unstable', batchId: 'one', runId: 'one-run', members: ['AGT-1'],
        atUtc: '2026-11-05T12:00:00Z', errorExcerpt: 'Error Message: timed out', flakyRerunPassed: true,
      }],
    }] });
    await Promise.resolve();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[data-testid="batch-failed-tests"]').textContent)
      .toContain('3 batches · 4 runs · 3 member sets');
    expect(fixture.nativeElement.querySelector('[data-testid="batch-failed-tests"]').textContent)
      .toContain('Flaky suspect');
    fixture.nativeElement.querySelector('[aria-label="Create fix card for Product.Tests.Unstable"]').click();
    http.expectOne('/api/watch-paths').flush([{ name: 'Demo', path: '/tmp/demo' }]);
    const followUps = http.match(() => true);
    expect(followUps.some(request => request.request.urlWithParams.includes('taskType=bug'))).toBe(true);
    for (const request of followUps) request.flush({}, { status: 500, statusText: 'Fixture unavailable' });
    await fixture.whenStable();
    expect(fixture.componentInstance['createForm'].visible()).toBe(true);
    expect(fixture.componentInstance['createForm'].newTaskType).toBe('bug');
    expect(fixture.componentInstance['createForm'].newPrompt).toContain('Batches: one, two, three.');
    expect(fixture.componentInstance['createForm'].newPrompt).toContain('run one-run');
  });

  it('shows missing evidence as an incomplete ranking instead of an empty history', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(BatchPanelComponent);
    fixture.componentRef.setInput('project', 'Demo');
    fixture.componentRef.setInput('closed', vi.fn());
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/batch-gate/state?history=10').flush(redHalvingState);
    http.expectOne('/api/projects/Demo/batch-gate/failed-tests?windowDays=7').flush({
      project: 'Demo', windowDays: 7, pendingEvidenceRuns: 1, tests: [],
    });
    await Promise.resolve();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[data-testid="batch-failure-evidence-pending"]').textContent)
      .toContain('evidence for 1 batch run is temporarily unavailable');
    expect(fixture.nativeElement.querySelector('[data-testid="batch-failed-tests"]').textContent)
      .not.toContain('No failed tests in this window.');
    http.verify();
  });
});
