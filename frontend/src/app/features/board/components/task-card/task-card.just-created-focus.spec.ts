import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskCardComponent } from './task-card.component';
import type { TaskInfo } from '../../../../models/task.model';
import { returnFocusToOpener } from '@studio/ui/directives/modal-focus.directive';

/**
 * AGT-3834: after Create the dialog closes and focus fell to the body. The
 * new card takes that focus so a keyboard user lands on what they created,
 * but never steals focus the user has already moved elsewhere.
 */
describe('TaskCardComponent just-created focus (AGT-3834)', () => {
  async function mount(highlightJobId: string | null) {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', makeJob('task-new'));
    fixture.componentRef.setInput('highlightJobId', highlightJobId);
    fixture.detectChanges();
    await fixture.whenStable();
    await Promise.resolve();
    return fixture.nativeElement.querySelector('[data-testid="task-card"]') as HTMLElement;
  }

  it('focuses the just-created card when focus was dropped to the body', async () => {
    (document.activeElement as HTMLElement | null)?.blur();
    const card = await mount('task-new');
    expect(document.activeElement).toBe(card);
  });

  it('leaves focus alone on other cards', async () => {
    (document.activeElement as HTMLElement | null)?.blur();
    const card = await mount('task-other');
    expect(document.activeElement).not.toBe(card);
  });

  it('takes the focus the closing create dialog handed back to its opener', async () => {
    const opener = document.createElement('button');
    const dialog = document.createElement('div');
    document.body.append(opener, dialog);
    try {
      returnFocusToOpener(opener, dialog);
      expect(document.activeElement).toBe(opener);
      const card = await mount('task-new');
      expect(document.activeElement).toBe(card);
    } finally {
      opener.remove();
      dialog.remove();
    }
  });

  it('keeps an opener the user refocused after the dialog closed', async () => {
    const opener = document.createElement('button');
    const dialog = document.createElement('div');
    document.body.append(opener, dialog);
    try {
      returnFocusToOpener(opener, dialog);
      opener.blur();
      opener.focus();
      await mount('task-new');
      expect(document.activeElement).toBe(opener);
    } finally {
      opener.remove();
      dialog.remove();
    }
  });

  it('does not steal focus the user already moved', async () => {
    const other = document.createElement('button');
    document.body.appendChild(other);
    other.focus();
    try {
      await mount('task-new');
      expect(document.activeElement).toBe(other);
    } finally {
      other.remove();
    }
  });
});

function makeJob(id: string): TaskInfo {
  return {
    id,
    taskKey: `test::${id}`,
    key: id.toUpperCase(),
    title: id,
    state: '1-preparation',
    order: 1,
    agent: 'codex',
    createdAt: '2026-11-01T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: `/tmp/watch/1-preparation/${id}`,
    lastActivity: '2026-11-01T09:00:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
  } as TaskInfo;
}
