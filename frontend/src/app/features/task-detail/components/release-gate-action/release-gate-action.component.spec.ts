import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { Subject } from 'rxjs';

import { ReleaseGateActionComponent, type ReleaseGateTarget } from './release-gate-action.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';

/**
 * AGT-2709. The release write cannot be painted optimistically - the flag is
 * what the scheduler reads, so a UI that shows "Released" before the write
 * landed would lie about whether the dependents can run. Style-guide rule R6
 * therefore applies: react immediately, disable while pending, expose
 * `aria-busy`, and only settle once the server answered.
 */
const TARGET: ReleaseGateTarget = {
  jobId: 'lib-acceptance',
  watchPath: '/ws/lib',
  key: 'LIB-1',
  released: false,
};

async function mount(target: ReleaseGateTarget, dependents: string[] = ['APP-1']) {
  const response = new Subject<{ released: boolean }>();
  const tasks = {
    setTaskReleased: vi.fn().mockReturnValue(response),
    refresh: vi.fn(),
  } as unknown as TaskService;
  const notifications = { success: vi.fn(), error: vi.fn() };

  await TestBed.configureTestingModule({
    imports: [ReleaseGateActionComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: TaskService, useValue: tasks },
      { provide: NotificationService, useValue: notifications },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(ReleaseGateActionComponent);
  fixture.componentRef.setInput('target', target);
  fixture.componentRef.setInput('dependents', dependents);
  fixture.detectChanges();
  const button = (fixture.nativeElement as HTMLElement)
    .querySelector('[data-testid="release-gate-toggle"]') as HTMLButtonElement;
  return { fixture, tasks, notifications, button, response };
}

describe('ReleaseGateActionComponent', () => {
  it('names the pending state and the dependents the decision unblocks', async () => {
    const { fixture } = await mount(TARGET, ['APP-1', 'APP-2']);
    const host: HTMLElement = fixture.nativeElement;

    expect(host.querySelector('[data-testid="release-gate-state"]')!.textContent)
      .toContain('Release pending');
    // One name plus a count keeps the row short; the full list is the tooltip.
    expect(host.querySelector('[data-testid="release-gate-dependents"]')!.textContent)
      .toContain('Unblocks APP-1 +1');
    expect(fixture.componentInstance.tooltip()).toContain('APP-2');
  });

  it('stays busy until the write answers and never paints ahead of it', async () => {
    const { fixture, button, response, notifications } = await mount(TARGET);
    const emitted: boolean[] = [];
    fixture.componentInstance.releasedChange.subscribe(v => emitted.push(v));

    button.click();
    fixture.detectChanges();

    expect(button.disabled).toBe(true);
    expect(button.getAttribute('aria-busy')).toBe('true');
    expect(emitted).toEqual([]);
    // The input is still `released: false`, so the label must not claim success.
    expect(fixture.componentInstance.stateLabel()).toBe('Release pending');

    response.next({ released: true });
    response.complete();
    fixture.detectChanges();

    expect(button.disabled).toBe(false);
    expect(button.getAttribute('aria-busy')).toBeNull();
    expect(emitted).toEqual([true]);
    expect(notifications.success).toHaveBeenCalled();
  });

  it('ignores a second click while a write is in flight', async () => {
    const { button, tasks } = await mount(TARGET);
    button.click();
    button.click();
    expect(tasks.setTaskReleased).toHaveBeenCalledTimes(1);
  });

  it('offers the withdrawal once the target is released', async () => {
    const { button, tasks } = await mount({ ...TARGET, released: true });
    expect(button.textContent).toContain('Withdraw release');
    button.click();
    expect(tasks.setTaskReleased).toHaveBeenCalledWith('lib-acceptance', false, '/ws/lib');
  });
});
