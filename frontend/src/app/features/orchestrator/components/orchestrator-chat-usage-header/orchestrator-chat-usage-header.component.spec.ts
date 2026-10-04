import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { OrchestratorChatUsageHeaderComponent } from './orchestrator-chat-usage-header.component';
import { UiPreferencesService } from '../../../shell/state/ui-preferences.service';

describe('OrchestratorChatUsageHeaderComponent', () => {
  async function makeFixture() {
    await TestBed.configureTestingModule({
      imports: [OrchestratorChatUsageHeaderComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const preferences = TestBed.inject(UiPreferencesService);
    preferences.chatMetadataOverride.set(null);
    preferences.chatMetadataProjectDefault.set(true);
    const fixture = TestBed.createComponent(OrchestratorChatUsageHeaderComponent);
    fixture.componentRef.setInput('turns', [{
      id: 'turn-1', ts: '2026-09-26T09:00:00Z', role: 'orchestrator', text: 'Done',
      metadata: { inputTokens: 10, cachedInputTokens: 20, outputTokens: 5, cost: 0.001 },
    }]);
    return { fixture, preferences, http: TestBed.inject(HttpTestingController) };
  }

  const url = (project: string) => `/api/projects/${encodeURIComponent(project)}/chat-metadata`;

  it('shows session totals and lets the user hide them', async () => {
    const { fixture } = await makeFixture();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('1 turn · 35 tokens · $0.0010');
    (fixture.nativeElement.querySelector('[data-testid="chat-metadata-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('35 tokens');
  });

  it('counts a legacy assistant turn and marks its usage and cost incomplete', async () => {
    const { fixture } = await makeFixture();
    fixture.componentRef.setInput('turns', [
      { id: 'user-1', ts: '2026-09-26T09:00:00Z', role: 'user', text: 'Question' },
      { id: 'turn-1', ts: '2026-09-26T09:00:01Z', role: 'orchestrator', text: 'Answer' },
    ]);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('1 turn · tokens incomplete · cost incomplete');
    expect(fixture.nativeElement.textContent).not.toContain('$0.0000');
  });

  it('does not present partial totals as complete when a legacy turn is mixed with a measured turn', async () => {
    const { fixture } = await makeFixture();
    fixture.componentRef.setInput('turns', [
      {
        id: 'turn-1', ts: '2026-09-26T09:00:00Z', role: 'orchestrator', text: 'Measured',
        metadata: {
          model: 'gpt-6-astra', providerThreadId: 'old-thread', host: 'runner-01',
          queuedAt: '2026-09-26T09:00:00Z', startedAt: '2026-09-26T09:00:02Z',
          finishedAt: '2026-09-26T09:00:10Z',
          inputTokens: 10, cachedInputTokens: 20, outputTokens: 5, cost: 0.001,
        },
      },
      { id: 'turn-2', ts: '2026-09-26T09:00:01Z', role: 'orchestrator', text: 'Legacy' },
    ]);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('2 turns · tokens incomplete · cost incomplete');
    expect(fixture.nativeElement.textContent).not.toContain('35 tokens');
    expect(fixture.nativeElement.textContent).not.toContain('$0.0010');
    expect(fixture.nativeElement.textContent).toContain('duration incomplete');
    expect(fixture.nativeElement.textContent).not.toContain('10s total');
    expect(fixture.nativeElement.textContent).not.toContain('session old-thread');
  });

  it('applies the project default returned by the settings endpoint', async () => {
    const { fixture, preferences, http } = await makeFixture();
    fixture.componentRef.setInput('project', 'Project A');
    fixture.detectChanges();
    http.expectOne(url('Project A')).flush({ chatMetadataEnabled: false });
    fixture.detectChanges();
    expect(preferences.chatMetadataEnabled()).toBe(false);
    expect(fixture.nativeElement.textContent).not.toContain('35 tokens');
    http.verify();
  });

  it('does not keep the previous project default when the next project request fails', async () => {
    const { fixture, preferences, http } = await makeFixture();
    fixture.componentRef.setInput('project', 'Project A');
    fixture.detectChanges();
    http.expectOne(url('Project A')).flush({ chatMetadataEnabled: false });
    expect(preferences.chatMetadataProjectDefault()).toBe(false);

    fixture.componentRef.setInput('project', 'Project B');
    fixture.detectChanges();
    // While Project B's default is loading, Project A's opt-out no longer applies.
    expect(preferences.chatMetadataProjectDefault()).toBe(true);
    http.expectOne(url('Project B')).flush(
      { error: "Unknown project 'Project B'" }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(preferences.chatMetadataProjectDefault()).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('1 turn · 35 tokens · $0.0010');
    http.verify();
  });

  it('consumes a network failure and falls back to the default-on setting', async () => {
    const { fixture, preferences, http } = await makeFixture();
    preferences.chatMetadataProjectDefault.set(false);
    fixture.componentRef.setInput('project', 'Project A');
    fixture.detectChanges();
    http.expectOne(url('Project A')).error(new ProgressEvent('error'));
    fixture.detectChanges();
    expect(preferences.chatMetadataProjectDefault()).toBe(true);
    http.verify();
  });

  it('treats a malformed response as the default-on setting', async () => {
    const { fixture, preferences, http } = await makeFixture();
    fixture.componentRef.setInput('project', 'Project A');
    fixture.detectChanges();
    http.expectOne(url('Project A')).flush({ chatMetadataEnabled: 'no' });
    expect(preferences.chatMetadataProjectDefault()).toBe(true);
    http.verify();
  });

  it('resets the project default when the chat leaves project scope', async () => {
    const { fixture, preferences, http } = await makeFixture();
    fixture.componentRef.setInput('project', 'Project A');
    fixture.detectChanges();
    http.expectOne(url('Project A')).flush({ chatMetadataEnabled: false });
    fixture.componentRef.setInput('project', null);
    fixture.detectChanges();
    expect(preferences.chatMetadataProjectDefault()).toBe(true);
    http.verify();
  });
});
