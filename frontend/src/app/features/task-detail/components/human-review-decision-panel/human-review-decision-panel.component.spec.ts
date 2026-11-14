import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { of, throwError, type Observable } from 'rxjs';
import { HumanReviewDecisionPanelComponent } from './human-review-decision-panel.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { AuthSessionState, type AuthStatus } from '../../../../services/auth.service';
import type { TaskInfo } from '../../../../models/task.model';

function session(reviewerProjects: string[] | undefined): AuthStatus {
  return {
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'usr_rui', username: 'rui', displayName: 'Rui Reviewer', role: 'viewer', projects: ['shop'],
      disabled: false, mustChangePassword: false, reviewerProjects },
  };
}

async function mount(
  state: string,
  /** `null`: a session without the Reviewer role. */
  reviewerProjects: string[] | null = ['Shop'],
  acceptHumanReview: () => Observable<unknown> = vi.fn(() => of({ decision: 'accept', state: '6-completed' })),
  sendBackHumanReview: () => Observable<unknown> = vi.fn(() => of({ decision: 'send-back', state: '2-ready', followUp: 'queued' })),
) {
  const refresh = vi.fn();
  const success = vi.fn();
  const error = vi.fn();
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [HumanReviewDecisionPanelComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: { acceptHumanReview, sendBackHumanReview, refresh } },
      { provide: NotificationService, useValue: { success, error } },
    ],
  }).compileComponents();
  TestBed.inject(AuthSessionState).status.set(session(reviewerProjects ?? undefined));
  const fixture = TestBed.createComponent(HumanReviewDecisionPanelComponent);
  fixture.componentRef.setInput('job', { id: 'SHOP-7', state, projectName: 'shop', watchPath: '/shop/tasks' } as TaskInfo);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const byId = <T extends HTMLElement>(id: string) => host.querySelector<T>(`[data-testid="${id}"]`);
  const type = (id: string, text: string) => {
    const field = byId<HTMLTextAreaElement>(id)!;
    field.value = text;
    field.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };
  const click = (id: string) => {
    byId<HTMLButtonElement>(id)!.click();
    fixture.detectChanges();
  };
  return { byId, type, click, acceptHumanReview, sendBackHumanReview, refresh, success, error };
}

describe('HumanReviewDecisionPanelComponent (AGT-3670)', () => {
  it('offers accept and send back on a Human Review card of a reviewed project', async () => {
    const { byId } = await mount('5-human-review');
    expect(byId('human-review-decision')?.textContent).toContain('Your review');
    expect(byId('human-review-accept')?.textContent).toContain('Accept');
    expect(byId('human-review-send-back-open')?.textContent).toContain('Send back with a note');
  });

  it.each([
    ['another lane', '2-ready', ['Shop']],
    ['a session without the Reviewer role', '5-human-review', null],
    ['another reviewed project', '5-human-review', ['Docs']],
  ])('stays away from %s', async (_label, state, projects) => {
    const { byId } = await mount(state, projects as string[] | null);
    expect(byId('human-review-decision')).toBeNull();
  });

  it('treats a workspace Reviewer binding as every project', async () => {
    const { byId } = await mount('5-human-review', ['*']);
    expect(byId('human-review-decision')).not.toBeNull();
  });

  it('accepts the card and refreshes the board', async () => {
    const { click, acceptHumanReview, refresh, success } = await mount('5-human-review');
    click('human-review-accept');

    expect(acceptHumanReview).toHaveBeenCalledWith('SHOP-7', '/shop/tasks');
    expect(success).toHaveBeenCalledWith('Accepted. The card moved to Accepted.');
    expect(refresh).toHaveBeenCalledWith(true);
  });

  it('requires a note, then sends the card back with it', async () => {
    const { byId, type, click, sendBackHumanReview, refresh, success } = await mount('5-human-review');
    click('human-review-send-back-open');
    const submit = byId<HTMLButtonElement>('human-review-send-back')!;
    expect(submit.disabled).toBe(true);
    type('human-review-note', '   ');
    expect(submit.disabled).toBe(true);
    type('human-review-note', ' The header is still loud. ');
    expect(submit.disabled).toBe(false);
    click('human-review-send-back');

    expect(sendBackHumanReview).toHaveBeenCalledWith('SHOP-7', 'The header is still loud.', '/shop/tasks');
    expect(success).toHaveBeenCalledWith('Sent back. The card moved to Ready with your note for the next run.');
    expect(refresh).toHaveBeenCalledWith(true);
    expect(byId('human-review-send-back-form')).toBeNull();
  });

  it('keeps the note and shows the server message when the send back is refused', async () => {
    const refused = vi.fn(() => throwError(() => ({
      status: 409,
      error: { error: 'not-in-human-review', message: 'Only a card in Human Review can be accepted or sent back.' },
    })));
    const { byId, type, click, refresh, error } = await mount('5-human-review', ['Shop'], undefined, refused);
    click('human-review-send-back-open');
    type('human-review-note', 'Use the quiet tone.');
    click('human-review-send-back');

    const message = 'Only a card in Human Review can be accepted or sent back.';
    expect(error).toHaveBeenCalledWith(message, 'Send back refused');
    expect(byId('human-review-error')?.textContent).toContain(message);
    expect(byId<HTMLTextAreaElement>('human-review-note')?.value).toBe('Use the quiet tone.');
    expect(refresh).not.toHaveBeenCalled();
  });
});
