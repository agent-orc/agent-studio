import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { of } from 'rxjs';
import { DecisionPanelComponent } from './decision-panel.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { type DecisionContent, type TaskInfo } from '../../../../models/task.model';

function decision(overrides: Partial<DecisionContent> = {}): DecisionContent {
  return {
    question: 'Ship with a lock file, or without?',
    options: [
      { id: 'a', label: 'Lock file', consequences: 'Reproducible' },
      { id: 'b', label: 'No lock file', risks: 'Drift' },
    ],
    recommendedOptionId: 'a',
    recommendationReason: 'Reproducibility wins',
    decider: 'operator',
    status: 'requested',
    ...overrides,
  };
}

function job(d: DecisionContent | null = decision(), kind: TaskInfo['kind'] = 'decision'): TaskInfo {
  return {
    id: 'decide-card',
    taskKey: 'agt::decide-card',
    key: 'AGT-2792',
    title: 'Stable release contract',
    state: '1-preparation',
    watchPath: '/ws/tasks',
    projectName: 'PROJ-002',
    folderPath: '/ws/tasks/1-preparation/decide-card',
    kind,
    decision: d,
  } as unknown as TaskInfo;
}

async function mount(info: TaskInfo, taskStub: Partial<TaskService> = {}) {
  await TestBed.configureTestingModule({
    imports: [DecisionPanelComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: taskStub },
      { provide: NotificationService, useValue: { success: vi.fn(), warning: vi.fn() } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(DecisionPanelComponent);
  fixture.componentRef.setInput('job', info);
  fixture.detectChanges();
  return fixture;
}

describe('DecisionPanelComponent', () => {
  it('renders nothing for a non-decision card', async () => {
    const fixture = await mount(job(decision(), 'task'));
    expect(fixture.nativeElement.querySelector('[data-testid="decision-panel"]')).toBeNull();
  });

  it('opens on the options with the recommendation marked and the decider shown', async () => {
    const fixture = await mount(job());
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="decision-question"]')?.textContent).toContain('lock file');
    expect(host.querySelectorAll('[data-testid="decision-option"]')).toHaveLength(2);
    expect(host.querySelector('[data-testid="decision-recommended"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="decision-decider"]')?.textContent).toContain('operator');
    // Choose actions are present while open.
    expect(host.querySelectorAll('[data-testid="decision-choose"]')).toHaveLength(2);
  });

  it('records a decision with the chosen option and rationale', async () => {
    const decideCard = vi.fn(() =>
      of({ decision: decision({ status: 'decided', chosenOptionId: 'b', rationale: 'simpler' }), targetState: '6-completed', unblocked: ['AGT-2793'], created: [] }));
    const fixture = await mount(job(), { decideCard } as unknown as Partial<TaskService>);
    const host = fixture.nativeElement as HTMLElement;

    (host.querySelectorAll('[data-testid="decision-choose"]')[1] as HTMLButtonElement).click();
    fixture.detectChanges();
    fixture.componentInstance.rationale.set('simpler');
    fixture.detectChanges();
    (host.querySelector('[data-testid="decision-submit"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(decideCard).toHaveBeenCalledWith('decide-card', 'b', 'simpler', '/ws/tasks');
    // The panel switches to the settled view.
    expect(host.querySelector('[data-testid="decision-settled"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="decision-chosen"]')).not.toBeNull();
  });

  it('shows the recorded choice and a reopen affordance once decided', async () => {
    const fixture = await mount(job(decision({
      status: 'decided', chosenOptionId: 'b', rationale: 'simpler', decidedBy: 'alice', recordPath: 'decision-record.md',
    })));
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelectorAll('[data-testid="decision-choose"]')).toHaveLength(0);
    expect(host.querySelector('[data-testid="decision-settled"]')?.textContent).toContain('simpler');
    expect(host.querySelector('[data-testid="decision-reopen"]')).not.toBeNull();
  });
});
