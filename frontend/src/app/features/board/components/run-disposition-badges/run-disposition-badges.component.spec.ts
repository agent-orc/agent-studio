import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { RunDispositionBadgesComponent } from './run-disposition-badges.component';

describe('RunDispositionBadgesComponent', () => {
  it('shows a live remote badge when a run remains active outside Progress', () => {
    TestBed.configureTestingModule({
      imports: [RunDispositionBadgesComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(RunDispositionBadgesComponent);
    fixture.componentRef.setInput('state', '2-ready');
    fixture.componentRef.setInput('execution', {
      state: 'remote-running',
      executionKind: 'remote',
      connectionState: 'connected',
      leaseState: 'active',
      trustReason: 'live lease',
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-live-remote-outside-progress"]')).not.toBeNull();

    fixture.componentRef.setInput('state', '3-progress');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-live-remote-outside-progress"]')).toBeNull();
  });
});
