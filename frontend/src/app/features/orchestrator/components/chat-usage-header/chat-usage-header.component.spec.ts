import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { ChatUsageHeaderComponent } from './chat-usage-header.component';

describe('ChatUsageHeaderComponent', () => {
  it('totals recorded turns and hides amounts on opt-out', async () => {
    await TestBed.configureTestingModule({
      imports: [ChatUsageHeaderComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ChatUsageHeaderComponent);
    fixture.componentRef.setInput('turns', [{
      id: 'reply', ts: '2026-09-26T10:00:00Z', role: 'orchestrator', text: 'Done',
      tokenUsage: { inputTokens: 10, outputTokens: 5, cacheReadTokens: 20, cacheCreationTokens: 0 },
      metadata: { totalLatencyMs: 5000, cost: 0.0042 },
    }]);
    fixture.componentRef.setInput('enabled', true);
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('35 tokens');
    expect(fixture.nativeElement.textContent).toContain('$0.0042');
    fixture.componentRef.setInput('enabled', false);
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).not.toContain('35 tokens');
  });
});
