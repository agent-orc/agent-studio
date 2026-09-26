import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { ChatLiveUsageComponent } from './chat-live-usage.component';

describe('ChatLiveUsageComponent', () => {
  it('shows active and heavy chat turns by project and host', async () => {
    await TestBed.configureTestingModule({
      imports: [ChatLiveUsageComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ChatLiveUsageComponent);
    TestBed.inject(HttpTestingController).expectOne('/api/runner/project-chat/usage').flush({
      items: [{ project: 'Agent Studio', host: 'agent-runner-01', queuedTurns: 1,
        activeTurns: 2, heavyTurns: 1, heavyAccountingUnknown: true,
        cpuShare: 0.5, cpuShareUnknown: false }],
    });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Agent Studio');
    expect(fixture.nativeElement.textContent).toContain('1 + unknown');
    expect(fixture.nativeElement.textContent).toContain('50%');
  });
});
