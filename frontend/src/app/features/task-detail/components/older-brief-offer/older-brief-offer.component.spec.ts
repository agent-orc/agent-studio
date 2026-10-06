import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { OlderBriefOfferComponent } from './older-brief-offer.component';

describe('OlderBriefOfferComponent', () => {
  it('renders all three decisions and submits the selected decision', () => {
    TestBed.configureTestingModule({
      imports: [OlderBriefOfferComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    const fixture = TestBed.createComponent(OlderBriefOfferComponent);
    fixture.componentRef.setInput('task', {
      id: 'AGT-3021',
      watchPath: '/test/project',
      olderBriefDelivery: {
        status: 'pending',
        attemptId: 'attempt-1',
        briefVersion: 'old',
        currentBriefVersion: 'new',
        offeredAtUtc: '2026-10-06T00:00:00Z',
        resultRef: 'refs/results/attempt-1',
      },
    } as TaskInfo);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="older-brief-accept"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="older-brief-starting-point"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="older-brief-discard"]')).not.toBeNull();

    fixture.nativeElement.querySelector('[data-testid="older-brief-discard"]').click();
    const request = TestBed.inject(HttpTestingController).expectOne(
      (candidate) => candidate.url.includes('/older-brief-delivery/decision') && candidate.method === 'POST',
    );
    expect(request.request.body.decision).toBe('discard');
    request.flush({ status: 'discard' });
    expect(fixture.componentInstance.pending()).toBeNull();
  });
});
