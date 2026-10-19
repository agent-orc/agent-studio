import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';

import { WorkspaceQuotaBoardState } from '../../state/workspace-quota-board-state.service';
import { TaskCardQuotaNoticeComponent } from './task-card-quota-notice';

describe('TaskCardQuotaNoticeComponent', () => {
  function render(projectName: string, active: boolean) {
    TestBed.configureTestingModule({ imports: [TaskCardQuotaNoticeComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(TaskCardQuotaNoticeComponent);
    fixture.componentRef.setInput('projectName', projectName);
    fixture.componentRef.setInput('active', active);
    fixture.detectChanges();
    return { fixture, board: TestBed.inject(WorkspaceQuotaBoardState), root: fixture.nativeElement as HTMLElement };
  }
  const notice = (root: HTMLElement) => root.querySelector('[data-testid="task-card-workspace-quota"]');

  it('shows the project refusal before the workspace refusal and clears with the board state', async () => {
    const { fixture, board, root } = render('alpha', true);
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

  it('stays hidden on cards whose work is already admitted', async () => {
    const { fixture, board, root } = render('alpha', false);
    board.setWorkspace('Workspace quota reached');
    await fixture.whenStable();
    expect(notice(root)).toBeNull();
  });
});
