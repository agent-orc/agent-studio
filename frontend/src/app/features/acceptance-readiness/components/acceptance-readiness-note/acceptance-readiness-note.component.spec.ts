import { beforeEach, describe, expect, it, vi } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { AcceptanceReadinessNoteComponent } from './acceptance-readiness-note.component';
import { AcceptanceReadinessStore } from '../../state/acceptance-readiness.store';
import { readinessItem, reviewCard } from '../../testing/acceptance-readiness.fixtures';
import type { AcceptanceReadinessItem } from '../../models/acceptance-readiness.model';

describe('AcceptanceReadinessNoteComponent', () => {
  let item: AcceptanceReadinessItem | null;
  const store = { itemFor: vi.fn(() => item), openRound: vi.fn() };

  beforeEach(() => {
    store.openRound.mockReset();
    TestBed.configureTestingModule({
      imports: [AcceptanceReadinessNoteComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AcceptanceReadinessStore, useValue: store },
      ],
    });
  });

  function render(card = reviewCard('b', { displayKey: 'AGT-77' })): HTMLElement {
    const fixture = TestBed.createComponent(AcceptanceReadinessNoteComponent);
    fixture.componentRef.setInput('job', card);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows a refused card\'s reason, the review report link, and the round offer', () => {
    item = readinessItem('b', 'review-concerns', { missing: 'lane.ts' });
    const host = render();

    const note = host.querySelector('[data-testid="acceptance-readiness-note"]')!;
    expect(note.getAttribute('data-readiness')).toBe('review-concerns');
    expect(note.getAttribute('data-tone')).toBe('warning');
    expect(host.querySelector('[data-testid="acceptance-readiness-reason"]')!.textContent)
      .toContain('The requirement-fit review returned concerns: The lane hides the reason.');
    expect(host.querySelector('[data-testid="acceptance-readiness-missing"]')!.textContent).toContain('lane.ts');
    expect(host.querySelector('[data-testid="acceptance-review-report"]')!.getAttribute('href'))
      .toContain('/tasks/AGT-77?view=evidence:protocol');

    const click = new MouseEvent('click', { bubbles: true });
    const parentClick = vi.fn();
    host.addEventListener('click', parentClick);
    host.querySelector<HTMLButtonElement>('[data-testid="acceptance-send-round"]')!.dispatchEvent(click);
    expect(store.openRound).toHaveBeenCalledOnce();
    expect(parentClick).not.toHaveBeenCalled();
  });

  it('offers no round for a ready card', () => {
    item = readinessItem('b', 'ready');
    const host = render();

    expect(host.querySelector('[data-testid="acceptance-readiness-note"]')!.getAttribute('data-tone')).toBe('success');
    expect(host.querySelector('[data-testid="acceptance-send-round"]')).toBeNull();
  });

  it('renders nothing outside Human Review or before a check', () => {
    item = readinessItem('b', 'ready');
    expect(render(reviewCard('b', { state: '6-completed' })).querySelector('[data-testid="acceptance-readiness-note"]')).toBeNull();
    item = null;
    expect(render().querySelector('[data-testid="acceptance-readiness-note"]')).toBeNull();
  });
});
