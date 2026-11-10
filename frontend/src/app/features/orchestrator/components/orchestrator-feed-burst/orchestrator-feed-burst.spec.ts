import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import type { OrchestratorLogEntry } from '../../models/orchestrator.model';
import type { FeedBurst } from '../orchestrator-feed/feed-bursts';
import { OrchestratorFeedBurstComponent } from './orchestrator-feed-burst';

const entry = (jobId: string): OrchestratorLogEntry => ({
  ts: '2026-11-09T09:23:00Z', kind: 'alert', topic: 'stall-intervention', summary: 'x', jobId, project: 'Demo',
});

function burst(entries: OrchestratorLogEntry[], cards: number): FeedBurst {
  return {
    key: 'k', kind: 'alert', topic: 'stall-intervention', label: 'AI Operator escalated auto-review: stalled',
    entries, cards, newestTs: '2026-11-09T09:23:04Z', oldestTs: '2026-11-09T09:23:00Z', projects: ['Demo'],
  };
}

describe('OrchestratorFeedBurstComponent (AGT-3585)', () => {
  it('is a disclosure button that announces its state and emits a toggle', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorFeedBurstComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorFeedBurstComponent);
    const value = burst([entry('a'), entry('b'), entry('a')], 2);
    fixture.componentRef.setInput('burst', value);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const button = root.querySelector<HTMLButtonElement>('[data-testid="orchestrator-feed-burst"]')!;
    expect(button.type).toBe('button');
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(button.firstElementChild?.tagName.toLowerCase()).toBe('app-disclosure-marker');
    expect(root.querySelector('[data-testid="orchestrator-feed-burst-count"]')?.textContent?.trim()).toBe('3 events · 2 cards');

    let toggled: FeedBurst | null = null;
    fixture.componentInstance.toggleRequest.subscribe(next => (toggled = next));
    button.click();
    expect(toggled).toBe(value);
  });
});
