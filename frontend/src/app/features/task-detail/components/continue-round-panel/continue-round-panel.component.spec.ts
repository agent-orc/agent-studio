import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { of, throwError, type Observable } from 'rxjs';
import { ContinueRoundPanelComponent } from './continue-round-panel.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import type { TaskInfo } from '../../../../models/task.model';

const SHA = '3f9c2a7b1d4e5f60718293a4b5c6d7e8f9012345';
const REF = `refs/heads/agent-studio/results/run_demo/fence-3/${SHA}`;
const BASE = { ref: REF, sha: SHA, source: 'latest-result', resultAttemptId: 'run_demo' };

async function mount(
  state: string,
  continueJob: () => Observable<unknown> = vi.fn(() => of({ status: 'queued', base: BASE })),
  getContinueRoundBase: () => Observable<unknown> = vi.fn(() => of({ base: BASE })),
) {
  const refresh = vi.fn();
  const success = vi.fn();
  const error = vi.fn();
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [ContinueRoundPanelComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: { continueJob, getContinueRoundBase, refresh } },
      { provide: NotificationService, useValue: { success, error } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(ContinueRoundPanelComponent);
  fixture.componentRef.setInput('job', { id: 'AGT-3582', state, watchPath: '/project/tasks' } as TaskInfo);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const byId = <T extends HTMLElement>(id: string) => host.querySelector<T>(`[data-testid="${id}"]`);
  const type = (id: string, text: string) => {
    const field = byId<HTMLInputElement>(id)!;
    field.value = text;
    field.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };
  const click = (id: string) => {
    byId<HTMLButtonElement>(id)!.click();
    fixture.detectChanges();
  };
  return { host, byId, type, click, continueJob, getContinueRoundBase, refresh, success, error };
}

describe('ContinueRoundPanelComponent (AGT-3582)', () => {
  it.each(['5-human-review', '6-completed'])('offers a round on a %s card', async (state) => {
    const { byId } = await mount(state);
    expect(byId('continue-round-open')).not.toBeNull();
  });

  it.each(['0-backlog', '2-ready', '3-progress', '5e-escalated'])('stays away from a %s card', async (state) => {
    const { byId } = await mount(state);
    expect(byId('continue-round-panel')).toBeNull();
  });

  it('shows the run base read only and offers the three modes', async () => {
    const { byId, click, getContinueRoundBase } = await mount('6-completed');
    click('continue-round-open');

    expect(getContinueRoundBase).toHaveBeenCalledWith('AGT-3582', '/project/tasks');
    expect(byId('continue-round-base-ref')?.textContent).toBe(REF);
    expect(byId('continue-round-base-sha')?.textContent).toBe(SHA.slice(0, 12));
    expect(byId('continue-round-base')?.textContent).toContain('latest result');
    expect(byId('continue-round-base')?.querySelector('input, textarea')).toBeNull();
    for (const mode of ['continue', 'steer', 'extend']) expect(byId(`continue-round-mode-${mode}`)).not.toBeNull();
  });

  it('says so when the card has no result ref', async () => {
    const { byId, click } = await mount('5-human-review', undefined, vi.fn(() => of({ base: null })));
    click('continue-round-open');
    expect(byId('continue-round-base')?.textContent).toContain('No recorded result ref');
  });

  it('requires a prompt and a reason, then sends the round with its mode', async () => {
    const { byId, type, click, continueJob, refresh, success } = await mount('6-completed');
    click('continue-round-open');
    const submit = byId<HTMLButtonElement>('continue-round-submit')!;

    type('continue-round-prompt', 'Tighten the summary wording.');
    expect(submit.disabled).toBe(true);
    type('continue-round-reason', '   ');
    expect(submit.disabled).toBe(true);
    type('continue-round-reason', ' Operator asked for a shorter summary. ');
    click('continue-round-mode-steer');
    expect(submit.disabled).toBe(false);
    click('continue-round-submit');

    expect(continueJob).toHaveBeenCalledWith('AGT-3582', 'Tighten the summary wording.', '/project/tasks',
      undefined, undefined, undefined, 'steer', undefined, 'Operator asked for a shorter summary.');
    expect(success).toHaveBeenCalledWith(`Round queued. It starts from ${SHA.slice(0, 12)}.`);
    expect(refresh).toHaveBeenCalledWith(true);
    expect(byId('continue-round-form')).toBeNull();
  });

  it('keeps the form and toasts the server message when the round is refused', async () => {
    const continueJob = vi.fn(() => throwError(() => ({
      status: 409,
      error: {
        error: 'batch-isolation-active',
        message: 'AGT-3582 is a member of batch flight flight-7, which is isolating a red result.',
        flight: 'flight-7',
      },
    })));
    const { byId, type, click, refresh, error } = await mount('5-human-review', continueJob);
    click('continue-round-open');
    type('continue-round-prompt', 'Fix the failing test.');
    type('continue-round-reason', 'The gate is red.');
    click('continue-round-submit');

    const message = 'AGT-3582 is a member of batch flight flight-7, which is isolating a red result.';
    expect(error).toHaveBeenCalledWith(message, 'Continue refused');
    expect(byId('continue-round-error')?.textContent).toContain(message);
    expect(byId('continue-round-form')).not.toBeNull();
    expect(refresh).not.toHaveBeenCalled();
  });
});
