import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { TaskInfo, TaskLiveQueue } from '../../../../models/task.model';
import { RemoteHostsService } from '../../../remote-hosts';
import { ReadyWaitSplitComponent } from './ready-wait-split.component';

function card(id: string, queue: TaskLiveQueue | null, extra: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id, key: id, taskKey: `demo::${id}`, title: id, state: '2-ready', order: 1, agent: 'codex',
    createdAt: '2026-11-09T09:00:00Z', watchPath: '/w', projectName: 'demo', folderPath: `/w/2-ready/${id}`,
    lastActivity: '2026-11-09T09:00:00Z', sessionName: null, model: null, cliType: 'codex', useOwnSession: null,
    lastUsage: null, execution: null, commit: null,
    liveStatus: { attempt: 1, activeStep: null, nextSteps: [], queue },
    ...extra,
  };
}

function render(jobs: TaskInfo[]): HTMLElement {
  TestBed.configureTestingModule({
    imports: [ReadyWaitSplitComponent],
    providers: [provideZonelessChangeDetection(), { provide: RemoteHostsService, useValue: { hosts: () => [] } }],
  });
  const fixture = TestBed.createComponent(ReadyWaitSplitComponent);
  fixture.componentRef.setInput('jobs', jobs);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('ReadyWaitSplitComponent (AGT-3579)', () => {
  it('renders one bucket per reason and a total equal to the card count', () => {
    const root = render([
      card('AGT-1', { kind: 'runner', position: 1, reasonCode: 'slots-full' }),
      card('AGT-2', { kind: 'runner', position: 2, reasonCode: 'slots-full' }),
      card('AGT-3', null, {
        pickupHold: { mechanism: 'dependency-gate', reason: 'waits', sinceUtc: '2026-11-09T09:00:00Z',
          unsatisfiable: false, resolutions: [] },
      }),
    ]);

    const split = root.querySelector('[data-testid="ready-wait-split"]')!;
    expect(split.getAttribute('data-total')).toBe('3');
    expect(split.getAttribute('aria-label')).toBe('Waiting: 1 dependency gate, 2 slots full');
    const slots = root.querySelector('[data-testid="ready-wait-bucket-runner-slots-full"]')!;
    expect(slots.getAttribute('data-count')).toBe('2');
    expect(slots.textContent?.trim()).toBe('2 slots full');
    expect(root.querySelector('[data-testid="ready-wait-bucket-hold-dependency-gate"]')?.getAttribute('data-count'))
      .toBe('1');
  });

  it('renders nothing when every card is plainly queued', () => {
    const root = render([card('AGT-1', { kind: 'runner', position: 1 }), card('AGT-2', null)]);
    expect(root.querySelector('[data-testid="ready-wait-split"]')).toBeNull();
  });
});
