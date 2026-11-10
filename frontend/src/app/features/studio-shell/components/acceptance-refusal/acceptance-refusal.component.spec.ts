import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { of, throwError } from 'rxjs';
import { AcceptanceRefusalComponent } from './acceptance-refusal.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import type { TaskInfo } from '../../../../models/task.model';
import { AcceptanceRefusalStore } from '../../../task-detail/runtime';

const JOB = { id: 'AGT-3649', taskKey: 'wp::AGT-3649', state: '5-human-review', watchPath: '/project/tasks',
  integration: { status: 'pending', integrationBranch: 'develop' } } as TaskInfo;

async function mount(job: TaskInfo, tasks: Partial<Record<'moveJob' | 'retryIntegration', unknown>> = {}) {
  const moveJob = vi.fn(() => of({}));
  const retryIntegration = vi.fn(() => of({}));
  const refresh = vi.fn();
  const success = vi.fn();
  const error = vi.fn();
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [AcceptanceRefusalComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: { moveJob, retryIntegration, refresh, ...tasks } },
      { provide: NotificationService, useValue: { success, error } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(AcceptanceRefusalComponent);
  fixture.componentRef.setInput('job', job);
  fixture.detectChanges();
  await fixture.whenStable();
  const host = fixture.nativeElement as HTMLElement;
  // The override form is portaled to <body> so the tab bar cannot clip it.
  const find = (testId: string) => document.querySelector<HTMLElement>(`[data-testid="${testId}"]`);
  const click = async (testId: string) => {
    find(testId)!.click();
    fixture.detectChanges();
    await fixture.whenStable();
  };
  const typeReason = (text: string) => {
    const input = find('acceptance-refusal-override-reason') as HTMLTextAreaElement;
    input.value = text;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };
  return { fixture, host, find, click, typeReason, moveJob, retryIntegration, refresh, success, error };
}

describe('AcceptanceRefusalComponent (AGT-3704)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('names the reason next to its one action', async () => {
    const { host } = await mount(JOB);
    expect(host.querySelector('[data-testid="acceptance-refusal-reason"]')?.textContent)
      .toContain('The delivery is not in develop yet.');
    expect(host.querySelector('[data-testid="acceptance-refusal-action"]')?.textContent?.trim())
      .toBe('Accept with override…');
  });

  it('accepts with an owner override only after a written reason', async () => {
    const { find, click, typeReason, moveJob, refresh, success } = await mount(JOB);
    TestBed.inject(AcceptanceRefusalStore).record({ taskKey: JOB.taskKey, code: 'integration-dead-end', message: 'x' });
    await click('acceptance-refusal-action');
    expect(find('acceptance-refusal-form')).not.toBeNull();
    expect((find('acceptance-refusal-submit') as HTMLButtonElement).disabled).toBe(true);

    typeReason('  Concept card: the Dossier is the delivery, nothing to integrate.  ');
    await click('acceptance-refusal-submit');

    expect(moveJob).toHaveBeenCalledWith('AGT-3649', '6-completed', '/project/tasks', undefined,
      'Concept card: the Dossier is the delivery, nothing to integrate.', true);
    expect(success).toHaveBeenCalled();
    expect(refresh).toHaveBeenCalledWith(true);
    expect(find('acceptance-refusal-form')).toBeNull();
    expect(TestBed.inject(AcceptanceRefusalStore).forTask(JOB.taskKey)).toBeNull();
  });

  it('keeps the form open with the server words when the override is refused', async () => {
    const moveJob = vi.fn(() => throwError(() => ({ status: 403,
      error: { error: 'operator-authority-required', message: 'Only an owner can override acceptance.' } })));
    const { find, click, typeReason } = await mount(JOB, { moveJob });
    await click('acceptance-refusal-action');
    typeReason('Accepting the concept Dossier.');
    await click('acceptance-refusal-submit');
    expect(find('acceptance-refusal-error')?.textContent).toContain('Only an owner can override acceptance.');
    expect(find('acceptance-refusal-form')).not.toBeNull();
  });

  it('retries a failed integration directly', async () => {
    const { click, retryIntegration, refresh, find } = await mount({ ...JOB, integration: {
      status: 'conflict-skipped', integrationBranch: 'develop',
      failure: { code: 'merge-conflict', label: 'Conflict', reason: 'develop moved under the delivery.' },
    } } as TaskInfo);
    await click('acceptance-refusal-action');
    expect(retryIntegration).toHaveBeenCalledWith('AGT-3649', '/project/tasks');
    expect(refresh).toHaveBeenCalledWith(true);
    expect(find('acceptance-refusal-form')).toBeNull();
  });
});
