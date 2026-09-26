import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import type { OrchestratorContextDigest } from '../../models/orchestrator.model';
import { OrchestratorSideSheetComponent } from './orchestrator-side-sheet.component';

describe('OrchestratorSideSheetComponent · ORCH-1 context digest', () => {
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

  function digest(contextKey: string, text = 'lanes: ready=2'): OrchestratorContextDigest {
    return {
      contextKey,
      capturedAt: new Date().toISOString(),
      digest: text,
      sources: [
        { name: 'lanes', status: 'ok', capturedAt: new Date().toISOString(), detail: null },
      ],
    };
  }

  function expectSessionsRequest(http: HttpTestingController) {
    const request = http.expectOne('/api/orchestrator/sessions');
    expect(request.request.method).toBe('GET');
    return request;
  }

  it('shows the assigned runner and queued time while a remote turn waits', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component.activeProject.set('Agent Studio');
    component.remoteChatStatus.set({
      contextKey: 'project:Agent Studio', state: 'queued', runnerId: 'agent-runner-01',
      queuedAt: '2026-09-26T07:18:40Z', reason: 'Provider unavailable',
    });
    fixture.detectChanges();
    expectSessionsRequest(http).flush({ sessions: [] });
    const waiting = fixture.nativeElement.querySelector('[data-testid="orchestrator-chat-waiting"]');
    expect(waiting?.textContent).toContain('agent-runner-01');
    expect(waiting?.textContent).toContain('Provider unavailable');
    expect(waiting?.textContent).toContain('since');
    component.remoteChatStatus.set({
      contextKey: 'project:Agent Studio', state: 'running', runnerId: 'agent-runner-01',
      queuedAt: '2026-09-26T07:18:40Z', reason: null,
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="orchestrator-chat-waiting"]')).toBeNull();
    fixture.destroy();
  });

  it('uses the visible Refresh action to force context, then reconcile chat and sessions', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component.activeProject.set('Agent Studio');
    fixture.detectChanges();
    expectSessionsRequest(http).flush({ sessions: [] });

    component.contextDigestState.selectContext('project:Agent Studio');
    component.contextDigestState.digest.set(digest('project:Agent Studio'));
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="orch-context-badge"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="orch-context-scope"]')?.textContent.trim())
      .toBe('Project context');
    expect(fixture.nativeElement.querySelector('[data-testid="orch-context-freshness"]')?.textContent)
      .toContain('Context captured');

    (fixture.nativeElement.querySelector('[data-testid="orch-side-sheet-refresh"]') as HTMLButtonElement).click();
    const contextRequest = http.expectOne('/api/orchestrator/context/project:Agent%20Studio/refresh');
    expect(contextRequest.request.method).toBe('POST');
    const chatRequest = http.expectOne('/api/runner/project:Agent%20Studio/orchestrator-chat');
    expect(chatRequest.request.method).toBe('GET');
    const sessionsRequest = expectSessionsRequest(http);
    contextRequest.flush(digest('project:Agent Studio', 'lanes: progress=1'));
    chatRequest.flush({
      project: 'Agent Studio',
      turns: [],
    });
    http.expectNone('/api/orchestrator/sessions');
    sessionsRequest.flush({ sessions: [] });

    expect(component.contextDigestState.digest()?.digest).toBe('lanes: progress=1');
    expect(component.contextDigestState.error()).toBeNull();
    http.verify();
    fixture.destroy();
  });

  it('preserves the last good digest when a forced refresh fails', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component.activeProject.set('demo-project');
    const previous = digest('project:demo-project');
    component.contextDigestState.selectContext('project:demo-project');
    component.contextDigestState.digest.set(previous);

    component.refreshCurrentContext();
    const contextRequest = http.expectOne('/api/orchestrator/context/project:demo-project/refresh');
    expect(contextRequest.request.method).toBe('POST');
    const chatRequest = http.expectOne('/api/runner/project:demo-project/orchestrator-chat');
    expect(chatRequest.request.method).toBe('GET');
    const sessionsRequest = expectSessionsRequest(http);
    contextRequest.flush(
      { error: 'quota probe unavailable' },
      { status: 503, statusText: 'Service Unavailable' },
    );
    chatRequest.flush({
      project: 'demo-project',
      turns: [],
    });
    http.expectNone('/api/orchestrator/sessions');
    sessionsRequest.flush({ sessions: [] });

    expect(component.contextDigestState.digest()).toBe(previous);
    expect(component.contextDigestState.error()).toBe('quota probe unavailable');
    expect(component.contextDigestState.statusText()).toContain('Refresh failed');
    http.verify();
    fixture.destroy();
  });

  it('does not let an older background read overwrite a forced refresh', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component.activeProject.set('demo-project');
    component.show();
    fixture.detectChanges();
    expectSessionsRequest(http).flush({ sessions: [] });
    const background = http.expectOne('/api/orchestrator/context/project:demo-project');
    expect(background.request.method).toBe('GET');
    const backgroundChat = http.expectOne('/api/runner/project:demo-project/orchestrator-chat');
    expect(backgroundChat.request.method).toBe('GET');
    backgroundChat.flush({
      project: 'demo-project',
      turns: [],
    });
    expectSessionsRequest(http).flush({ sessions: [] });
    http.expectNone('/api/orchestrator/sessions');

    component.refreshCurrentContext();
    const forcedContext = http.expectOne('/api/orchestrator/context/project:demo-project/refresh');
    expect(forcedContext.request.method).toBe('POST');
    const forcedChat = http.expectOne('/api/runner/project:demo-project/orchestrator-chat');
    expect(forcedChat.request.method).toBe('GET');
    const forcedSessions = expectSessionsRequest(http);
    forcedContext.flush(digest('project:demo-project', 'new forced digest'));
    forcedChat.flush({
      project: 'demo-project',
      turns: [],
    });
    http.expectNone('/api/orchestrator/sessions');
    forcedSessions.flush({ sessions: [] });
    background.flush(digest('project:demo-project', 'older background digest'));

    expect(component.contextDigestState.digest()?.digest).toBe('new forced digest');
    http.verify();
    fixture.destroy();
  });

  it('loads the global digest on open without enabling a global transcript', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component.activeProject.set('previous-project');
    fixture.detectChanges();
    expectSessionsRequest(http).flush({ sessions: [] });

    component.selectChatContext('global');
    component.show();
    fixture.detectChanges();
    expect(component.effectiveProject()).toBeNull();
    expect(component.effectiveJobId()).toBeNull();
    http.expectOne('/api/orchestrator/context/global').flush(digest('global', 'workspace digest'));
    http.expectNone('/api/runner/global/orchestrator-chat');
    fixture.detectChanges();

    expect(component.contextDigestState.scopeLabel()).toBe('Global context');
    expect(fixture.nativeElement.querySelector('[data-testid="orchestrator-global-chat-empty"]'))
      .toBeTruthy();
    http.verify();
    fixture.destroy();
  });

  it('self-heals a stale selection and shares the spaced task key across digest, chat and send', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('projects', ['Agent Studio']);
    fixture.componentRef.setInput('activeJobId', 'agent-studio-task');
    fixture.componentRef.setInput('activeJobTitle', 'Repair orchestrator context');
    fixture.componentRef.setInput('activeJobKey', 'AGT-2149');
    component.activeProject.set('Agent Studio');
    fixture.detectChanges();
    expectSessionsRequest(http).flush({ sessions: [] });

    component.selectedContextKey.set('task:old-project/AGT-1/invalid');
    fixture.detectChanges();
    expect(component.selectedContextKey()).toBeNull();
    expect(component.contextKey()).toBe('task:Agent Studio/AGT-2149');

    component.refreshCurrentContext();
    const contextRequest = http.expectOne('/api/orchestrator/context/task:Agent%20Studio/AGT-2149/refresh');
    expect(contextRequest.request.method).toBe('POST');
    const chatRequest = http.expectOne('/api/runner/task:Agent%20Studio/AGT-2149/orchestrator-chat');
    expect(chatRequest.request.method).toBe('GET');
    const sessionsRequest = expectSessionsRequest(http);
    contextRequest.flush(digest('task:Agent Studio/AGT-2149'));
    chatRequest.flush({ project: 'Agent Studio', turns: [] });
    http.expectNone('/api/orchestrator/sessions');
    sessionsRequest.flush({ sessions: [] });

    await component.onSubmit({ text: 'Please continue', attachments: [] });
    const send = http.expectOne('/api/runner/task:Agent%20Studio/AGT-2149/orchestrator-chat');
    expect(send.request.method).toBe('POST');
    expect(send.request.body.navigationContext).toMatchObject({
      currentTaskId: 'agent-studio-task',
      currentTaskKey: 'AGT-2149',
    });
    expect(send.request.body.contextEnvelope.scope).toMatchObject({
      kind: 'task',
      contextKey: 'task:Agent Studio/AGT-2149',
      projectId: 'Agent Studio',
      taskKey: 'AGT-2149',
    });
    send.flush({ project: 'Agent Studio', reply: { id: 'reply', role: 'orchestrator', text: 'Done' } });
    const reconciliation = http.expectOne('/api/runner/task:Agent%20Studio/AGT-2149/orchestrator-chat');
    expect(reconciliation.request.method).toBe('GET');
    const activityReconciliation = expectSessionsRequest(http);
    reconciliation.flush({ project: 'Agent Studio', turns: [] });
    activityReconciliation.flush({ sessions: [] });

    http.verify();
    fixture.destroy();
  });
});
