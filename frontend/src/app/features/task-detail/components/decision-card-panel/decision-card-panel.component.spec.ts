import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it, vi } from 'vitest';
import { of, throwError } from 'rxjs';
import { DecisionCardPanelComponent } from './decision-card-panel.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { ClientService } from '../../../../services/client.service';
import { PublicDemoModeService } from '../../../../services/public-demo-mode.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import type { DecisionContent, TaskInfo } from '../../../../models/task.model';

function decision(overrides: Partial<DecisionContent> = {}): DecisionContent {
  return {
    question: 'Ship the Stable release with a lock file, or without?',
    options: [
      { id: 'a', label: 'Lock file back', consequences: 'Reproducible installs', effort: 'S', risk: 'Low' },
      { id: 'b', label: 'Identity without lock file', consequences: 'Simpler release contract' },
    ],
    recommendedOptionId: 'a',
    recommendationReason: 'Reproducibility is the release contract.',
    decider: 'operator',
    status: 'pending',
    dependants: ['AGT-2793'],
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
    kind,
    decision: d,
  } as unknown as TaskInfo;
}

async function mount(info: TaskInfo, taskStub: Partial<TaskService> = {}, readOnly = false) {
  const notifications = { success: vi.fn(), warning: vi.fn(), info: vi.fn() };
  const navigation = { openReferenceOrNotify: vi.fn(() => true) };
  await TestBed.configureTestingModule({
    imports: [DecisionCardPanelComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: taskStub },
      { provide: NotificationService, useValue: notifications },
      { provide: ClientService, useValue: { byId: signal(new Map([['alice', { displayName: 'Alice' }]])) } },
      { provide: PublicDemoModeService, useValue: { readOnly: signal(readOnly) } },
      { provide: TaskReferenceNavigationService, useValue: navigation },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(DecisionCardPanelComponent);
  fixture.componentRef.setInput('job', info);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const q = (id: string) => host.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const qa = (id: string) => Array.from(host.querySelectorAll(`[data-testid="${id}"]`)) as HTMLElement[];
  return { fixture, host, q, qa, notifications, navigation };
}

function typeInto(el: HTMLElement | null, value: string): void {
  const input = el as HTMLTextAreaElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('DecisionCardPanelComponent (AGT-2795)', () => {
  it('renders nothing on a non-decision card', async () => {
    const { q } = await mount(job(decision(), 'task'));
    expect(q('decision-card-panel')).toBeNull();
  });

  it('opens on the question and options, with consequences, the recommendation, and one Choose per option', async () => {
    const { q, qa } = await mount(job());
    expect(q('decision-card-question')?.textContent).toContain('lock file');
    expect(q('decision-card-decider')?.textContent).toContain('Operator');
    const options = qa('decision-card-option');
    expect(options).toHaveLength(2);
    expect(options[0].getAttribute('data-recommended')).toBe('true');
    expect(options[0].textContent).toContain('Reproducible installs');
    expect(options[0].textContent).toContain('Risk');
    expect(q('decision-card-recommended')).not.toBeNull();
    expect(q('decision-card-recommendation-reason')?.textContent).toContain('Reproducibility');
    expect(qa('decision-card-choose')).toHaveLength(2);
    expect(q('decision-card-rationale-input')).not.toBeNull();
    expect(q('decision-card-dependants')?.textContent).toContain('AGT-2793');
  });

  it('records the chosen option with the typed rationale in one click and shows the receipt', async () => {
    const decided = decision({
      status: 'decided', chosenOptionId: 'b', rationale: 'Simpler to operate',
      decidedBy: 'alice', decidedAt: '2026-09-25T07:52:00Z', recordPath: 'operations/decisions/AGT-2792.md',
    });
    const decideCard = vi.fn(() => of({ decision: decided, targetState: '6-completed' }));
    const { fixture, q, qa, notifications } = await mount(job(), { decideCard } as unknown as Partial<TaskService>);
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);

    typeInto(q('decision-card-rationale-input'), '  Simpler to operate ');
    fixture.detectChanges();
    qa('decision-card-choose')[1].click();
    fixture.detectChanges();

    expect(decideCard).toHaveBeenCalledWith('decide-card', 'b', 'Simpler to operate', '/ws/tasks');
    expect(changed).toHaveBeenCalledOnce();
    expect(notifications.success).toHaveBeenCalledWith(expect.stringContaining('Unblocked AGT-2793'));
    expect(qa('decision-card-choose')).toHaveLength(0);
    const receipt = q('decision-card-receipt');
    expect(receipt?.textContent).toContain('Decided: b');
    expect(receipt?.textContent).toContain('Identity without lock file');
    expect(q('decision-card-rationale')?.textContent).toContain('Simpler to operate');
    expect(q('decision-card-decided-by')?.textContent).toContain('Alice');
    expect(q('decision-card-decided-at')?.textContent).toContain('2026-09-25 07:52Z');
    expect(q('decision-card-record-link')?.getAttribute('href'))
      .toBe('#/projects/proj-002/wiki?page=operations%2Fdecisions%2FAGT-2792.md');
    expect(q('decision-card-chosen')).not.toBeNull();
  });

  it('shows the backend refusal inline when the caller is not the named decider', async () => {
    const decideCard = vi.fn(() => throwError(() => new HttpErrorResponse({
      status: 403, error: { error: 'This decision is assigned to another client or role.' },
    })));
    const { fixture, q, qa } = await mount(job(decision({ decider: 'alice' })), { decideCard } as unknown as Partial<TaskService>);
    qa('decision-card-choose')[0].click();
    fixture.detectChanges();
    expect(q('decision-card-error')?.textContent).toContain('assigned to another client or role');
    expect(qa('decision-card-choose')).toHaveLength(2);
  });

  it('reopens only with a note and sends it to the reopen API', async () => {
    const reopenDecision = vi.fn(() => of({ decision: decision({ reopenNote: 'New evidence' }), targetState: '1-preparation' }));
    const { fixture, q, qa } = await mount(job(decision({
      status: 'decided', chosenOptionId: 'a', rationale: 'r', decidedBy: 'operator', decidedAt: '2026-09-25T07:52:00Z',
    })), { reopenDecision } as unknown as Partial<TaskService>);

    q('decision-card-reopen')!.click();
    fixture.detectChanges();
    const confirm = q('decision-card-reopen-confirm') as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);

    typeInto(q('decision-card-reopen-note-input'), 'New evidence');
    fixture.detectChanges();
    expect(confirm.disabled).toBe(false);
    confirm.click();
    fixture.detectChanges();

    expect(reopenDecision).toHaveBeenCalledWith('decide-card', 'New evidence', '/ws/tasks');
    expect(qa('decision-card-choose')).toHaveLength(2);
    expect(q('decision-card-reopen-note')?.textContent).toContain('New evidence');
  });

  it('keeps earlier cycles visible after a reopen', async () => {
    const { q } = await mount(job(decision({
      reopenNote: 'New evidence',
      history: [
        { status: 'decided', optionId: 'a', rationale: 'first call', actor: 'operator', at: '2026-09-20T10:00:00Z' },
        { status: 'reopened', actor: 'alice', at: '2026-09-21T10:00:00Z', note: 'New evidence' },
      ],
    })));
    const history = q('decision-card-history')?.textContent ?? '';
    expect(history).toContain('Reopened');
    expect(history).toContain('Alice');
    expect(history).toContain('Decided a · Lock file back');
    expect(history).toContain('first call');
  });

  it('keeps typed text and the recorded decision when a poll refreshes the same card', async () => {
    const decided = decision({ status: 'decided', chosenOptionId: 'a', decidedBy: 'alice', decidedAt: '2026-09-25T07:52:00Z' });
    const decideCard = vi.fn(() => of({ decision: decided, targetState: '6-completed' }));
    const { fixture, q, qa } = await mount(job(), { decideCard } as unknown as Partial<TaskService>);
    const refresh = async (title: string) => {
      // A poll hands over a fresh TaskInfo and decision object for the same card and status.
      fixture.componentRef.setInput('job', { ...job(), title });
      fixture.detectChanges();
      await fixture.whenStable();
    };

    typeInto(q('decision-card-rationale-input'), 'Half typed');
    fixture.detectChanges();
    await refresh('Renamed');
    expect(fixture.componentInstance.rationale()).toBe('Half typed');
    expect((q('decision-card-rationale-input') as HTMLTextAreaElement).value).toBe('Half typed');

    qa('decision-card-choose')[0].click();
    fixture.detectChanges();
    // The reload has not landed yet: a stale poll still says pending.
    await refresh('Stale poll');
    expect(q('decision-card-receipt')).not.toBeNull();
    expect(q('decision-card-decided-by')?.textContent).toContain('Alice');
    expect(qa('decision-card-choose')).toHaveLength(0);
  });

  it('clears typed text when another card or a new decision state arrives', async () => {
    const { fixture, q } = await mount(job());
    const input = () => q('decision-card-rationale-input') as HTMLTextAreaElement;

    typeInto(input(), 'Meant for the first card');
    fixture.detectChanges();
    fixture.componentRef.setInput('job', { ...job(), id: 'other-card', taskKey: 'agt::other-card' });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.componentInstance.rationale()).toBe('');
    expect(input().value).toBe('');

    typeInto(input(), 'Typed before the reopen landed');
    fixture.detectChanges();
    fixture.componentRef.setInput('job', job(decision({ status: 'requested' })));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.componentInstance.rationale()).toBe('');
  });

  it('disables Choose in the read-only public demo', async () => {
    const { qa } = await mount(job(), {}, true);
    expect(qa('decision-card-choose').every((b) => (b as HTMLButtonElement).disabled)).toBe(true);
  });

  it('opens a dependant from the Blocks line', async () => {
    const { fixture, host, navigation } = await mount(job());
    (host.querySelector('[data-testid="decision-card-dependants"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(navigation.openReferenceOrNotify).toHaveBeenCalledWith('AGT-2793');
  });
});
