import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
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
});
