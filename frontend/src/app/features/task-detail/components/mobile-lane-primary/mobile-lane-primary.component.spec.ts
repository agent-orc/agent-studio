import { afterEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import type { TaskInfo } from '../../../../models/task.model';
import { TASK_PHONE_QUERY } from '../../state/phone-layout';
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
  afterEach(() => vi.unstubAllGlobals());

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

  const refused: MobileLanePrimary = { ...accept, refusal: {
    reason: 'The requirement-fit review returned concerns.', action: 'review-again', actionLabel: 'Review again',
    source: 'readiness', replacesAccept: true, secondaryAction: { action: 'override', actionLabel: 'Accept with a reason…' },
  } };

  async function mountRefused(phone: boolean): Promise<HTMLElement> {
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: phone && query === TASK_PHONE_QUERY, media: query,
      addEventListener: () => undefined, removeEventListener: () => undefined,
    }));
    await TestBed.configureTestingModule({
      imports: [MobileLanePrimaryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(MobileLanePrimaryComponent);
    fixture.componentRef.setInput('job', { id: 'DEMO-19', taskKey: 'wp::DEMO-19', state: '5-human-review', watchPath: '/wp' } as TaskInfo);
    fixture.componentRef.setInput('primary', refused);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows a content refusal with its next step instead of Accept (AGT-3830)', async () => {
    const root = await mountRefused(true);
    expect(root.querySelector('[data-testid="studio-mobile-triage-action-mark-done"]')).toBeNull();
    expect(root.querySelector('[data-testid="acceptance-refusal-reason"]')?.textContent)
      .toContain('The requirement-fit review returned concerns.');
    expect(root.querySelector('[data-testid="acceptance-refusal-action"]')?.textContent?.trim()).toBe('Review again');
    expect(root.querySelector('[data-testid="acceptance-refusal-override"]')).not.toBeNull();
  });

  it('leaves the refusal to the toolbar outside the phone layout, so only one exists (AGT-3830)', async () => {
    const root = await mountRefused(false);
    expect(root.querySelector('[data-testid="acceptance-refusal"]')).toBeNull();
  });
});
