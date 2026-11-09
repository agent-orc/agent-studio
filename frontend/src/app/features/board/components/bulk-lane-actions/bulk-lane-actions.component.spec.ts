import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { BoardMutationsService } from '../../state/board-mutations.service';
import { BulkLaneActionsComponent } from './bulk-lane-actions.component';

const card = (id: string) => ({ id, taskKey: `demo::${id}`, title: id, state: '5e-escalated', watchPath: '/tmp/demo' }) as TaskInfo;

async function render(tasks: TaskInfo[], missing = 0, showReason = false) {
  await TestBed.configureTestingModule({
    imports: [BulkLaneActionsComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
  }).compileComponents();
  const fixture = TestBed.createComponent(BulkLaneActionsComponent);
  fixture.componentRef.setInput('tasks', tasks);
  fixture.componentRef.setInput('missing', missing);
  fixture.componentRef.setInput('showReason', showReason);
  fixture.detectChanges();
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

describe('BulkLaneActionsComponent (AGT-3585)', () => {
  it('renders one primary action first and runs the bulk move for every card', async () => {
    const tasks = [card('a'), card('b')];
    const { root } = await render(tasks);
    const buttons = [...root.querySelectorAll<HTMLButtonElement>('[data-testid^="bulk-action-"]')];
    expect(buttons.map(button => button.textContent?.trim())).toEqual(['Continue (reissue)', 'Resolve manually']);
    expect(buttons.map(button => button.classList.contains('bulk-actions__button--primary'))).toEqual([true, false]);
    expect(buttons.every(button => button.type === 'button')).toBe(true);

    const bulkMove = vi.spyOn(TestBed.inject(BoardMutationsService), 'bulkMove').mockResolvedValue(false);
    buttons[0].click();
    expect(bulkMove).toHaveBeenCalledWith(tasks, '2-ready', 'Continue (reissue)');
  });

  it('says why nothing is offered only where asked to', async () => {
    const quiet = await render([card('a')], 1);
    expect(quiet.root.textContent?.trim()).toBe('');
    TestBed.resetTestingModule();
    const explained = await render([card('a')], 1, true);
    expect(explained.root.querySelector('[data-testid="bulk-lane-actions-reason"]')?.textContent)
      .toContain('1 of these cards is no longer on the board');
  });
});
