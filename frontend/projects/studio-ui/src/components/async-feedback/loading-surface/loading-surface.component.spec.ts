import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { LoadingSurfaceComponent } from './loading-surface.component';

describe('LoadingSurfaceComponent', () => {
  let fixture: ComponentFixture<LoadingSurfaceComponent>;

  beforeEach(async () => {
    vi.useFakeTimers();
    await TestBed.configureTestingModule({ imports: [LoadingSurfaceComponent] }).compileComponents();
    fixture = TestBed.createComponent(LoadingSurfaceComponent);
    fixture.componentRef.setInput('kind', 'board');
    fixture.componentRef.setInput('label', 'Loading board…');
    fixture.detectChanges();
  });

  afterEach(() => vi.useRealTimers());

  it('suppresses feedback for loads that finish inside 200 ms', () => {
    vi.advanceTimersByTime(199);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="loading-surface-board"]')).toBeNull();
  });

  it('shows skeletons after 200 ms and context after one second', () => {
    vi.advanceTimersByTime(200);
    fixture.detectChanges();
    const surface = fixture.nativeElement.querySelector('[data-testid="loading-surface-board"]');
    expect(surface).not.toBeNull();
    expect(surface.textContent).not.toContain('Loading board…');

    vi.advanceTimersByTime(800);
    fixture.detectChanges();
    expect(surface.textContent).toContain('Loading board…');
  });
});
