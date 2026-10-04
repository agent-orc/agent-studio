import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { OrchestratorChatWaitingComponent } from './orchestrator-chat-waiting.component';

describe('OrchestratorChatWaitingComponent', () => {
  beforeEach(() => vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] }));

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  it('polls while sending, shows queued status, and stops polling when sending ends', () => {
    TestBed.configureTestingModule({
      imports: [OrchestratorChatWaitingComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(OrchestratorChatWaitingComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('project', 'demo-project');
    fixture.componentRef.setInput('contextKey', 'project:demo-project');
    fixture.componentRef.setInput('sending', true);
    fixture.detectChanges();

    vi.advanceTimersByTime(2_000);
    const request = http.expectOne(req => req.url === '/api/runner/project-chat/status');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('projectName')).toBe('demo-project');
    expect(request.request.params.get('contextKey')).toBe('project:demo-project');
    request.flush({
      state: 'queued', runnerId: 'runner-1', hostName: null,
      queuedAt: '2026-10-03T10:00:00Z', startedAt: null, reason: 'Runner busy',
    });
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Waiting for runner-1');

    fixture.componentRef.setInput('sending', false);
    fixture.detectChanges();
    vi.advanceTimersByTime(2_000);
    http.expectNone('/api/runner/project-chat/status');
    http.verify();

    fixture.destroy();
    vi.advanceTimersByTime(2_000);
    http.expectNone('/api/runner/project-chat/status');
  });
});
