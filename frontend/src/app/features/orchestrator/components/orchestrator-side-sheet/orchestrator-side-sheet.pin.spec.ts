import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { OrchestratorSideSheetComponent } from './orchestrator-side-sheet.component';

/**
 * MC-2 (Concept §4) unit coverage for the navigation-following context and
 * the Pin freeze. Drives the component's public signals directly (the same
 * pattern as the context-chip spec) so the effective-context derivation and
 * the pin snapshot are pinned without a full host-app render. `detectChanges`
 * is deliberately never called so the preferred-sync / refresh effects do not
 * clobber the manually-seeded `activeProject`.
 */
describe('OrchestratorSideSheetComponent · navigation context + pin', () => {
  beforeEach(() => sessionStorage.removeItem('atp.studio.orchestratorOpen.v1'));

  async function makeFixture() {
    await TestBed.configureTestingModule({
      imports: [OrchestratorSideSheetComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    return TestBed.createComponent(OrchestratorSideSheetComponent);
  }

  // AGT-2970: the chat usage header reads the project's chat-metadata default.
  function flushChatMetadataDefault(http: HttpTestingController, project: string) {
    const requests = http.match(`/api/projects/${encodeURIComponent(project)}/chat-metadata`);
    expect(requests.length).toBeGreaterThan(0);
    for (const request of requests.filter(pending => !pending.cancelled)) {
      expect(request.request.method).toBe('GET');
      request.flush({ chatMetadataEnabled: true });
    }
  }

  it('derives a project context on the board and a task context on a task page', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;

    c.activeProject.set('demo-project');
    expect(c.contextKind()).toBe('project');
    expect(c.contextKey()).toBe('project:demo-project');

    fixture.componentRef.setInput('activeJobId', 'job-1');
    fixture.componentRef.setInput('activeJobTitle', 'Fix the header');
    fixture.componentRef.setInput('activeJobKey', 'AGT-1916');
    expect(c.contextKind()).toBe('task');
    expect(c.contextKey()).toBe('task:demo-project/AGT-1916');
  });

  it('effective scope follows live navigation inputs while unpinned', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;

    c.activeProject.set('demo-project');
    fixture.componentRef.setInput('activeJobId', 'job-1');
    fixture.componentRef.setInput('activeJobTitle', 'Fix the header');
    fixture.componentRef.setInput('activeJobKey', 'AGT-1916');
    fixture.componentRef.setInput('activeJobState', '3-progress');

    expect(c.effectiveProject()).toBe('demo-project');
    expect(c.effectiveJobId()).toBe('job-1');
    expect(c.effectiveJobTitle()).toBe('Fix the header');
    expect(c.effectiveJobKey()).toBe('AGT-1916');
    expect(c.effectiveJobState()).toBe('3-progress');
  });

  it('uses the footer active-tab task while async detail inputs still describe the board', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;

    c.activeProject.set('stale-project');
    fixture.componentRef.setInput('activeJobId', null);
    fixture.componentRef.setInput('activeJobKey', null);
    fixture.componentRef.setInput('composerContext', {
      project: 'Quality Studio',
      surface: 'Task',
      detail: 'QS-54',
      taskKey: 'QS-54',
      taskId: 'qs-54-folder',
      taskTitle: 'Explain status.md relevance',
      taskState: '3-progress',
      taskWatchPath: '/tasks/quality-studio',
    });

    expect(c.contextKey()).toBe('task:Quality Studio/QS-54');
    expect(c.effectiveJobId()).toBe('qs-54-folder');
    expect(c.effectiveJobTitle()).toBe('Explain status.md relevance');
    expect(c.effectiveJobState()).toBe('3-progress');
  });

  it('sends the stable task key and full navigation context on every task follow-up', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    const route = '/api/runner/task:Quality%20Studio/QS-54/orchestrator-chat';

    c.activeProject.set('stale-project');
    fixture.componentRef.setInput('composerContext', {
      project: 'Quality Studio',
      surface: 'Task',
      detail: 'QS-54',
      taskKey: 'QS-54',
      taskId: 'qs-54-folder',
      taskTitle: 'Explain status.md relevance',
      taskState: '3-progress',
    });

    for (const text of ['Why is status.md relevant?', 'What does it say now?']) {
      await c.onSubmit({ text, attachments: [] } as never);
      const send = http.expectOne(route);
      expect(send.request.method).toBe('POST');
      expect(send.request.body.navigationContext).toMatchObject({
        currentPage: 'task-detail',
        currentTaskId: 'qs-54-folder',
        currentTaskKey: 'QS-54',
        currentTaskTitle: 'Explain status.md relevance',
        currentTaskState: '3-progress',
      });
      expect(send.request.body.contextEnvelope).toMatchObject({
        scope: {
          kind: 'task',
          contextKey: 'task:Quality Studio/QS-54',
          projectId: 'Quality Studio',
          taskKey: 'QS-54',
        },
        activeSurface: {
          kind: 'task',
          reference: 'QS-54',
          taskKey: 'QS-54',
        },
      });
      expect(send.request.body.contextEnvelope.capturedAt)
        .toBe(send.request.body.navigationContext.viewportTimestamp);
      send.flush({ project: 'Quality Studio', reply: { id: 'reply', role: 'orchestrator', text: 'ok' } });

      const read = http.expectOne(route);
      expect(read.request.method).toBe('GET');
      read.flush({ project: 'Quality Studio', turns: [] });
    }

    for (const sessions of http.match('/api/orchestrator/sessions')) {
      expect(sessions.request.method).toBe('GET');
      sessions.flush({ sessions: [] });
    }
    flushChatMetadataDefault(http, 'Quality Studio');
    http.verify();
  });

  it('refreshes the central context list when opening a task context for the first time', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    const contextKey = 'task:Quality Studio/QS-54';

    c.activeProject.set('stale-project');
    fixture.componentRef.setInput('composerContext', {
      project: 'Quality Studio',
      surface: 'Task',
      detail: 'QS-54',
      taskKey: 'QS-54',
      taskId: 'qs-54-folder',
      taskTitle: 'Explain status.md relevance',
      taskState: '3-progress',
    });

    c.refresh();
    http.expectOne('/api/runner/task:Quality%20Studio/QS-54/orchestrator-chat')
      .flush({ project: 'Quality Studio', turns: [] });
    http.expectOne('/api/orchestrator/sessions').flush({
      sessions: [{
        contextKey,
        kind: 'task',
        projectId: 'Quality Studio',
        taskKey: 'QS-54',
        title: 'QS-54',
        summary: 'Explain status.md relevance',
        updatedAt: '2026-08-10T12:00:00Z',
      }],
    });

    expect(c.contextSessions().map(session => session.contextKey)).toContain(contextKey);
    http.verify();
  });

  it('pin freezes the context so later navigation does not switch it', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;

    c.activeProject.set('demo-project');
    fixture.componentRef.setInput('activeJobId', 'job-1');
    fixture.componentRef.setInput('activeJobTitle', 'Fix the header');
    fixture.componentRef.setInput('activeJobKey', 'AGT-1916');
    fixture.componentRef.setInput('activeJobState', '3-progress');

    c.togglePin();
    expect(c.pinned()).toBe(true);
    const frozenKey = c.contextKey();
    expect(frozenKey).toBe('task:demo-project/AGT-1916');

    // Navigate elsewhere: the host swaps in a different task, and the picker
    // is inert while pinned. The effective scope must not move.
    fixture.componentRef.setInput('activeJobId', 'job-2');
    fixture.componentRef.setInput('activeJobTitle', 'Something else');
    fixture.componentRef.setInput('activeJobKey', 'AGT-2000');
    c.setActiveProject('other-project');

    expect(c.effectiveProject()).toBe('demo-project');
    expect(c.effectiveJobId()).toBe('job-1');
    expect(c.effectiveJobTitle()).toBe('Fix the header');
    expect(c.contextKey()).toBe(frozenKey);
    // The picker was blocked, so the live project signal never moved either.
    expect(c.activeProject()).toBe('demo-project');
  });

  it('unpin resumes following navigation', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;

    c.activeProject.set('demo-project');
    fixture.componentRef.setInput('activeJobId', 'job-1');
    fixture.componentRef.setInput('activeJobTitle', 'Fix the header');
    fixture.componentRef.setInput('activeJobKey', 'AGT-1916');

    c.togglePin();
    // Navigation moves on while pinned...
    fixture.componentRef.setInput('activeJobId', 'job-2');
    fixture.componentRef.setInput('activeJobTitle', 'Something else');
    fixture.componentRef.setInput('activeJobKey', 'AGT-2000');

    c.togglePin();
    expect(c.pinned()).toBe(false);
    // Effective scope now reflects the live inputs again.
    expect(c.effectiveJobId()).toBe('job-2');
    expect(c.effectiveJobTitle()).toBe('Something else');
    expect(c.contextKey()).toBe('task:demo-project/AGT-2000');
  });

  it('context label tracks the effective frozen scope while pinned', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;

    c.activeProject.set('demo-project');
    fixture.componentRef.setInput('activeJobId', 'job-1');
    fixture.componentRef.setInput('activeJobTitle', 'Fix the header');
    fixture.componentRef.setInput('activeJobKey', 'AGT-1916');
    c.togglePin();

    // Host navigates to the board of another project.
    c.setActiveProject('other-project');
    fixture.componentRef.setInput('activeJobId', null);
    fixture.componentRef.setInput('activeJobTitle', null);

    expect(c.contextChipText()).toBe(`Context: demo-project · Task 'Fix the header'`);
  });

  // MC-2: the chat body must actually READ the per-context thread, not just
  // derive the key. A task page reads the `task:<PROJ>/<KEY>` route; the board
  // reads the `project:<PROJ>` route. This is the wiring the B-grade review
  // flagged as missing ("chat still reads the project thread").
  it('refresh reads the task-context transcript route on a task page', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);

    c.activeProject.set('demo-project');
    fixture.componentRef.setInput('activeJobId', 'job-1');
    fixture.componentRef.setInput('activeJobTitle', 'Fix the header');
    fixture.componentRef.setInput('activeJobKey', 'AGT-1916');
    expect(c.contextKey()).toBe('task:demo-project/AGT-1916');

    c.refresh(true);
    const req = http.expectOne('/api/runner/task:demo-project/AGT-1916/orchestrator-chat');
    expect(req.request.method).toBe('GET');
    req.flush({
      project: 'demo-project',
      turns: [{ id: 't1', ts: '2026-07-09T00:00:00Z', role: 'user', text: 'task thread' }],
    });
    const sessions = http.expectOne('/api/orchestrator/sessions');
    expect(sessions.request.method).toBe('GET');
    sessions.flush({ sessions: [{
      contextKey: 'task:demo-project/AGT-1916',
      kind: 'task',
      projectId: 'demo-project',
      taskKey: 'AGT-1916',
      updatedAt: '2026-08-10T10:15:00Z',
      model: null,
      cumulativeInputTokens: 0,
      cumulativeOutputTokens: 0,
      cumulativeCacheReadTokens: 0,
      cumulativeCacheCreationTokens: 0,
      runtimeStatus: 'idle',
      queuePosition: 0,
      summary: 'Fix the header',
    }] });
    expect(c.turns().map((t) => t.text)).toEqual(['task thread']);
    expect(c.contextSessions().map((session) => session.contextKey))
      .toContain('task:demo-project/AGT-1916');
    http.verify();
  });

  it('refresh reads the project-context transcript route on the board', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);

    c.activeProject.set('demo-project');
    expect(c.contextKey()).toBe('project:demo-project');

    c.refresh(true);
    const req = http.expectOne('/api/runner/project:demo-project/orchestrator-chat');
    expect(req.request.method).toBe('GET');
    req.flush({ project: 'demo-project', turns: [] });
    const sessions = http.expectOne('/api/orchestrator/sessions');
    expect(sessions.request.method).toBe('GET');
    sessions.flush({ sessions: [] });
    http.verify();
  });
});
