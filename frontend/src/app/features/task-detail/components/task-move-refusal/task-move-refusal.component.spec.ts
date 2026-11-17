import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { AcceptanceReadinessStore } from '../../../acceptance-readiness';
import { readinessItem } from '../../../acceptance-readiness/testing/acceptance-readiness.fixtures';
import { AcceptanceRefusalActions } from '../../state/acceptance-refusal-actions.service';
import type { TaskMoveRefusal } from '../../state/triage-controller.service';
import { TaskMoveRefusalComponent } from './task-move-refusal.component';

const SUMMARY = 'The 830-card first-paint target remains unmet and lacks idle-host confirmation';
const CARD = {
  id: 'AGT-1', taskKey: 'test::AGT-1', state: '5-human-review', watchPath: '/wp',
  reviewProjection: { attempts: [{ aspects: [
    { aspect: 'requirement-fit', status: 'concerns', classification: 'product', summary: SUMMARY },
  ] }] },
} as unknown as TaskInfo;
const CONTENT: TaskMoveRefusal = {
  taskKey: CARD.taskKey, overridable: true,
  message: 'Acceptance refused: requirement-fit verdict was concerns. Accept it with a written reason, review it '
    + 'again against the current brief, or send a round to close the gap under Acceptance next steps.',
};

async function mount(refusal: TaskMoveRefusal, job: TaskInfo = CARD) {
  const actions = { openOverride: vi.fn(), reviewAgain: vi.fn() };
  await TestBed.configureTestingModule({
    imports: [TaskMoveRefusalComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      { provide: AcceptanceRefusalActions, useValue: actions }],
  }).compileComponents();
  const fixture = TestBed.createComponent(TaskMoveRefusalComponent);
  fixture.componentRef.setInput('refusal', refusal);
  fixture.componentRef.setInput('job', job);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const get = (testId: string) => el.querySelector<HTMLElement>(`[data-testid="${testId}"]`);
  return { fixture, el, get, actions };
}

describe('TaskMoveRefusalComponent (AGT-3873)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('names the review concern of a content refusal and offers the override and a review', async () => {
    const { get, actions } = await mount(CONTENT);
    expect(get('task-move-refusal')?.getAttribute('role')).toBe('alert');
    expect(get('task-move-refusal')?.textContent).toContain('Acceptance refused: requirement-fit verdict was concerns.');
    expect(get('task-move-refusal-concern')?.textContent?.replace(/\s+/g, ' ').trim())
      .toBe(`The review's concern: ${SUMMARY}`);

    get('task-move-refusal-override')!.click();
    expect(actions.openOverride).toHaveBeenCalledWith(CARD);
    expect(get('task-move-refusal-override')?.textContent?.trim()).toBe('Accept with a written reason');
    get('task-move-refusal-review')!.click();
    expect(actions.reviewAgain).toHaveBeenCalledWith(CARD);
    expect(get('task-move-refusal-review')?.textContent?.trim()).toBe('Review again');
  });

  it('takes the concern from the dry run once it answers', async () => {
    const bare = { ...CARD, reviewProjection: null } as unknown as TaskInfo;
    const { fixture, get } = await mount(CONTENT, bare);
    expect(get('task-move-refusal-concern')).toBeNull();
    const store = TestBed.inject(AcceptanceReadinessStore);
    vi.spyOn(store, 'itemFor').mockReturnValue(readinessItem('AGT-1', 'review-concerns', { concern: SUMMARY }));
    fixture.componentRef.setInput('job', { ...bare });
    fixture.detectChanges();
    expect(get('task-move-refusal-concern')?.textContent).toContain(SUMMARY);
  });

  it('offers no buttons for a refusal it cannot resolve, and disables them while mutations are blocked', async () => {
    const { get } = await mount({ taskKey: CARD.taskKey, message: 'The task stayed in Human review. Try again.' });
    expect(get('task-move-refusal-override')).toBeNull();
    expect(get('task-move-refusal-concern')).toBeNull();
    TestBed.resetTestingModule();

    const blocked = await mount(CONTENT);
    blocked.fixture.componentRef.setInput('mutationsBlocked', true);
    blocked.fixture.detectChanges();
    expect((blocked.get('task-move-refusal-override') as HTMLButtonElement).disabled).toBe(true);
    expect((blocked.get('task-move-refusal-review') as HTMLButtonElement).disabled).toBe(true);
  });
});
