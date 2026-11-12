import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { Subject, of, throwError, type Observable } from 'rxjs';
import { ContinueRoundPanelComponent } from './continue-round-panel.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import type { TaskInfo } from '../../../../models/task.model';

const SHA = '3f9c2a7b1d4e5f60718293a4b5c6d7e8f9012345';
const REF = `refs/heads/agent-studio/results/run_demo/fence-3/${SHA}`;
const BASE = { ref: REF, sha: SHA, source: 'latest-result', resultAttemptId: 'run_demo' };

const GUARD_MESSAGE = "This card is in 'concept' mode and its pipeline is read only, and this follow-up reads like a "
  + 'code-change request. Keep the card in concept mode and send the follow-up anyway, or promote the card to implementation.';
const guardRefusal = () => ({ status: 409, error: { error: 'continue-mode-guard', message: GUARD_MESSAGE } });
const PLAN = {
  source: { repoRelativePath: 'docs/concepts/naming/index.html', title: 'Result file naming' },
  items: [{ title: 'Rename result files', promptMarkdown: '...' }, { title: 'Migrate old results', promptMarkdown: '...' }],
  mode: 'coding', targetState: '1-preparation', watchPath: '/project/tasks', projectName: 'demo',
};

async function mount(
  state: string,
  continueJob: (...args: unknown[]) => Observable<unknown> = vi.fn(() => of({ status: 'queued', base: BASE })),
  getContinueRoundBase: () => Observable<unknown> = vi.fn(() => of({ base: BASE })),
  extra: { mode?: string; getPromoteConcept?: () => Observable<unknown>; promoteConcept?: () => Observable<unknown> } = {},
) {
  const getPromoteConcept = extra.getPromoteConcept ?? vi.fn(() => of(PLAN));
  const promoteConcept = extra.promoteConcept ?? vi.fn(() => of({
    source: PLAN.source, created: [{ jobId: 'agt-3701', taskKey: 'AGT-3701', title: 'Rename result files' }],
  }));
  const refresh = vi.fn();
  const success = vi.fn();
  const error = vi.fn();
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [ContinueRoundPanelComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: { continueJob, getContinueRoundBase, refresh, getPromoteConcept, promoteConcept } },
      { provide: NotificationService, useValue: { success, error } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(ContinueRoundPanelComponent);
  fixture.componentRef.setInput('job', { id: 'AGT-3582', state, watchPath: '/project/tasks', mode: extra.mode } as TaskInfo);
  const promoteToCoding = vi.fn();
  fixture.componentInstance.promoteToCoding.subscribe(promoteToCoding);
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
  const fill = (prompt: string, reason = 'Owner follow-up from sight review.') => {
    click('continue-round-open');
    type('continue-round-prompt', prompt);
    type('continue-round-reason', reason);
  };
  return {
    host, byId, type, click, fill, fixture, continueJob, getContinueRoundBase, refresh, success, error,
    getPromoteConcept, promoteConcept, promoteToCoding,
  };
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

  describe('concept and planning mode refusal (AGT-3700)', () => {
    const PROMPT = 'Please add one more section D3 File naming to the Dossier.';

    it('offers keep-and-send and promote as buttons and never an API path', async () => {
      const continueJob = vi.fn(() => throwError(guardRefusal));
      const { byId, fill, click, error } = await mount('5-human-review', continueJob, undefined, { mode: 'concept' });
      fill(PROMPT);
      click('continue-round-submit');

      expect(byId('continue-mode-refusal-message')?.textContent).toContain('promote the card to implementation');
      expect(byId('continue-mode-refusal')?.textContent).not.toContain('/api/');
      expect(byId('continue-mode-refusal')?.textContent).not.toContain('modeOverride');
      expect(byId('continue-mode-keep-and-send')?.textContent?.trim()).toBe('Keep in concept mode and send');
      expect(byId('continue-mode-promote')?.textContent?.trim()).toBe('Promote to implementation');
      expect(byId('continue-round-error')).toBeNull();
      expect(error).not.toHaveBeenCalled();
    });

    it('keeps the card in concept mode and sends the same round with modeOverride=concept', async () => {
      const continueJob = vi.fn()
        .mockReturnValueOnce(throwError(guardRefusal))
        .mockReturnValueOnce(of({ status: 'queued', base: BASE }));
      const { byId, fill, click, success } = await mount('5-human-review', continueJob, undefined, { mode: 'concept' });
      fill(PROMPT);
      click('continue-round-submit');
      click('continue-mode-keep-and-send');

      expect(continueJob).toHaveBeenLastCalledWith('AGT-3582', PROMPT, '/project/tasks',
        undefined, undefined, undefined, 'continue', 'concept', 'Owner follow-up from sight review.');
      expect(success).toHaveBeenCalled();
      expect(byId('continue-round-form')).toBeNull();
    });

    it('names the planning mode on a planning card and hands promotion to the title block', async () => {
      const { byId, fill, click, promoteToCoding, getPromoteConcept } = await mount(
        '6-completed', vi.fn(() => throwError(guardRefusal)), undefined, { mode: 'planning' });
      fill('Implement the retry limit change.');
      click('continue-round-submit');

      expect(byId('continue-mode-keep-and-send')?.textContent?.trim()).toBe('Keep in planning mode and send');
      click('continue-mode-promote');
      expect(promoteToCoding).toHaveBeenCalledTimes(1);
      expect(getPromoteConcept).not.toHaveBeenCalled();
    });

    it('promotes a concept card from its Dossier proposals after confirmation', async () => {
      const { byId, fill, click, getPromoteConcept, promoteConcept, success, refresh } = await mount(
        '5-human-review', vi.fn(() => throwError(guardRefusal)), undefined, { mode: 'concept' });
      fill(PROMPT);
      click('continue-round-submit');
      click('continue-mode-promote');

      expect(getPromoteConcept).toHaveBeenCalledWith('AGT-3582', '/project/tasks');
      expect(byId('continue-mode-promote-items')?.textContent).toContain('Rename result files');
      expect(byId('continue-mode-promote-confirm')?.textContent?.trim()).toBe('Create 2 implementation cards');
      expect(promoteConcept).not.toHaveBeenCalled();
      click('continue-mode-promote-confirm');

      expect(promoteConcept).toHaveBeenCalledWith('AGT-3582', [0, 1], '/project/tasks');
      expect(success).toHaveBeenCalledWith('Created 1 implementation card: AGT-3701.');
      expect(refresh).toHaveBeenCalledWith(true);
      expect(byId('continue-round-form')).toBeNull();
    });

    it('shows why a concept card cannot be promoted yet and keeps the keep-and-send choice', async () => {
      const getPromoteConcept = vi.fn(() => throwError(() => ({
        status: 409, error: { error: 'The concept Dossier is missing or did not pass concept review.' },
      })));
      const { byId, fill, click } = await mount('5-human-review', vi.fn(() => throwError(guardRefusal)), undefined,
        { mode: 'concept', getPromoteConcept });
      fill(PROMPT);
      click('continue-round-submit');
      click('continue-mode-promote');

      expect(byId('continue-mode-promote-error')?.textContent).toContain('did not pass concept review');
      expect(byId('continue-mode-keep-and-send')).not.toBeNull();
    });

    // The AGT-3649 F7 shape: the refusal arrives after the button went
    // pending, so the disabled state flips while the request is in flight.
    it('re-enables Send round after an asynchronous refusal and when the text changes', async () => {
      const answers = [new Subject<unknown>(), new Subject<unknown>()];
      let call = 0;
      const continueJob = vi.fn(() => answers[call++]);
      const { byId, fill, click, type, fixture } = await mount('5-human-review', continueJob, undefined, { mode: 'concept' });
      const submit = () => byId<HTMLButtonElement>('continue-round-submit')!;
      fill(PROMPT);
      click('continue-round-submit');
      expect(submit().disabled).toBe(true);

      answers[0].error(guardRefusal());
      fixture.detectChanges();
      expect(byId('continue-mode-refusal')).not.toBeNull();
      expect(submit().disabled).toBe(false);

      type('continue-round-prompt', 'Please write one more chapter on result naming.');
      expect(byId('continue-mode-refusal')).toBeNull();
      expect(submit().disabled).toBe(false);
      click('continue-round-submit');
      expect(continueJob).toHaveBeenCalledTimes(2);

      answers[1].error({ status: 409, error: { error: 'batch-isolation-active', message: 'Batch flight-7 is isolating.' } });
      fixture.detectChanges();
      expect(byId('continue-round-error')?.textContent).toContain('Batch flight-7 is isolating.');
      expect(submit().disabled).toBe(false);
      type('continue-round-prompt', 'Please write one more chapter on result naming, briefly.');
      expect(submit().disabled).toBe(false);
    });
  });
});
