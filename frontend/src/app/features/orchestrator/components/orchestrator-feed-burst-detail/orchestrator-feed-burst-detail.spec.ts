import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import type { OrchestratorLogEntry } from '../../models/orchestrator.model';
import { OrchestratorFeedBurstDetailComponent } from './orchestrator-feed-burst-detail';

const entry = (jobId: string): OrchestratorLogEntry => ({
  ts: '2026-11-09T09:23:00Z', kind: 'alert', topic: 'stall-intervention', summary: 'x', jobId, project: 'Demo', watchPath: '/tmp/demo',
});
const card = (id: string, state = '5e-escalated') =>
  ({ id, taskKey: id.toUpperCase(), title: `Title ${id}`, state, watchPath: '/tmp/demo' }) as TaskInfo;

async function render(entries: OrchestratorLogEntry[], jobs: TaskInfo[]) {
  await TestBed.configureTestingModule({
    imports: [OrchestratorFeedBurstDetailComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
  }).compileComponents();
  TestBed.inject(TaskService).jobs.set(jobs);
  const fixture = TestBed.createComponent(OrchestratorFeedBurstDetailComponent);
  fixture.componentRef.setInput('burst', {
    key: 'k', kind: 'alert', topic: 'stall-intervention', label: 'AI Operator escalated auto-review: stalled',
    entries, cards: 2, newestTs: '2026-11-09T09:23:04Z', oldestTs: '2026-11-09T09:23:00Z', projects: ['Demo'],
  });
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('OrchestratorFeedBurstDetailComponent (AGT-3585)', () => {
  it('lists each card once with its lane and offers the actions valid for all of them', async () => {
    const root = await render([entry('a'), entry('b'), entry('a')], [card('a'), card('b')]);
    expect(root.querySelector('[data-testid="orchestrator-burst-card-count"]')?.textContent?.trim()).toBe('2');
    const cards = [...root.querySelectorAll<HTMLElement>('[data-testid="orchestrator-burst-card"]')];
    expect(cards).toHaveLength(2);
    expect(cards.map(row => row.querySelector('[data-testid="orchestrator-burst-card-lane"]')?.textContent?.trim()))
      .toEqual(['Escalated', 'Escalated']);
    expect(root.querySelectorAll('[data-testid^="bulk-action-"]')).toHaveLength(2);
  });

  it('withholds the bulk actions and says why when cards left the lane', async () => {
    const root = await render([entry('a'), entry('b')], [card('a'), card('b', '2-ready')]);
    expect(root.querySelector('[data-testid="bulk-lane-actions"]')).toBeNull();
    expect(root.querySelector('[data-testid="bulk-lane-actions-reason"]')?.textContent?.trim())
      .toBe('These cards sit in different lanes now; open them one by one.');
  });
});
