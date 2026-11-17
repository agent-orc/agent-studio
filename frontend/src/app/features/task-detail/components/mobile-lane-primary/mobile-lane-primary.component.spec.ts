import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import type { TaskInfo } from '../../../../models/task.model';
import { MobileLanePrimaryComponent, type MobileLanePrimary } from './mobile-lane-primary.component';

const accept: MobileLanePrimary = {
  primaryId: 'mark-done',
  primaryLabel: 'Accept',
  primaryTooltip: 'Accept this task',
  awaitingGit: false,
  blockedByIntegration: false,
  actingId: null,
};

describe('MobileLanePrimaryComponent', () => {
  it('uses the lane action and disables it when delivery is held', async () => {
    await TestBed.configureTestingModule({
      imports: [MobileLanePrimaryComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(MobileLanePrimaryComponent);
    const pressed: boolean[] = [];
    fixture.componentInstance.pressed.subscribe(() => pressed.push(true));
    fixture.componentRef.setInput('primary', accept);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const button = root.querySelector<HTMLButtonElement>('[data-testid="studio-mobile-triage-action-mark-done"]')!;
    expect(button.textContent).toContain('Accept');
    expect(button.disabled).toBe(false);
    button.click();
    expect(pressed).toHaveLength(1);

    fixture.componentRef.setInput('primary', { ...accept, blockedByIntegration: true });
    fixture.detectChanges();
    expect(button.disabled).toBe(true);
    button.click();
    expect(pressed).toHaveLength(1);
  });

  it('shows a content refusal with its next step instead of Accept (AGT-3830)', async () => {
    await TestBed.configureTestingModule({
      imports: [MobileLanePrimaryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(MobileLanePrimaryComponent);
    fixture.componentRef.setInput('job', { id: 'DEMO-19', taskKey: 'wp::DEMO-19', state: '5-human-review', watchPath: '/wp' } as TaskInfo);
    fixture.componentRef.setInput('primary', { ...accept, refusal: {
      reason: 'The requirement-fit review returned concerns.', action: 'review-again', actionLabel: 'Review again',
      source: 'readiness', replacesAccept: true, secondaryAction: { action: 'override', actionLabel: 'Accept with a reason…' },
    } });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="studio-mobile-triage-action-mark-done"]')).toBeNull();
    expect(root.querySelector('[data-testid="acceptance-refusal-reason"]')?.textContent)
      .toContain('The requirement-fit review returned concerns.');
    expect(root.querySelector('[data-testid="acceptance-refusal-action"]')?.textContent?.trim()).toBe('Review again');
    expect(root.querySelector('[data-testid="acceptance-refusal-override"]')).not.toBeNull();
  });
});
