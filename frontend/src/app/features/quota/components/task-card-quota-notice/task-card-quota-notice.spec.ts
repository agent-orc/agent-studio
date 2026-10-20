import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';

import { TaskState } from '../../../../models/task.model';
import { WorkspaceQuotaBoardState } from '../../state/workspace-quota-board-state.service';
import { TaskCardQuotaNoticeComponent } from './task-card-quota-notice';

describe('TaskCardQuotaNoticeComponent', () => {
  function render(state: string, running = false) {
    TestBed.configureTestingModule({ imports: [TaskCardQuotaNoticeComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(TaskCardQuotaNoticeComponent);
    fixture.componentRef.setInput('task', { projectName: 'alpha', state });
    fixture.componentRef.setInput('running', running);
    fixture.detectChanges();
    return { fixture, board: TestBed.inject(WorkspaceQuotaBoardState), root: fixture.nativeElement as HTMLElement };
  }
  const notice = (root: HTMLElement) => root.querySelector('[data-testid="task-card-workspace-quota"]');

  it('shows the project refusal before the workspace refusal and clears with the board state', async () => {
    const { fixture, board, root } = render(TaskState.Ready);
    expect(notice(root)).toBeNull();

    board.setWorkspace('Workspace quota reached');
    await fixture.whenStable();
    expect(notice(root)?.textContent).toContain('Workspace quota reached');

    board.setProject('alpha', 'Project alpha quota reached');
    await fixture.whenStable();
    expect(notice(root)?.textContent).toContain('Project alpha quota reached');

    board.reset();
    await fixture.whenStable();
    expect(notice(root)).toBeNull();
  });

  it('stays hidden on a running card whose work is already admitted', async () => {
    const { fixture, board, root } = render(TaskState.Progress, true);
    board.setWorkspace('Workspace quota reached');
    await fixture.whenStable();
    expect(notice(root)).toBeNull();

    fixture.componentRef.setInput('running', false);
    await fixture.whenStable();
    expect(notice(root)?.textContent).toContain('Workspace quota reached');
  });

  it('stays hidden on completed and archived cards', async () => {
    const { fixture, board, root } = render(TaskState.Completed);
    board.setWorkspace('Workspace quota reached');
    await fixture.whenStable();
    expect(notice(root)).toBeNull();

    fixture.componentRef.setInput('task', { projectName: 'alpha', state: TaskState.Archive });
    await fixture.whenStable();
    expect(notice(root)).toBeNull();
  });
});
