import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { OrchestratorChatUsageHeaderComponent } from './orchestrator-chat-usage-header.component';
import { UiPreferencesService } from '../../../shell/state/ui-preferences.service';

describe('OrchestratorChatUsageHeaderComponent', () => {
  it('shows session totals and lets the user hide them', async () => {
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
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('1 turn · 35 tokens · $0.0010');
    (fixture.nativeElement.querySelector('[data-testid="chat-metadata-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('35 tokens');
  });
});
