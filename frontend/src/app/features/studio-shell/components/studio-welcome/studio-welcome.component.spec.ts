import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { StudioWelcomeComponent } from './studio-welcome.component';

describe('StudioWelcomeComponent', () => {
  it('keeps the empty state factual and promotes project chat without direct task creation', async () => {
    await TestBed.configureTestingModule({
      imports: [StudioWelcomeComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();

    const fixture = TestBed.createComponent(StudioWelcomeComponent);
    fixture.componentRef.setInput('projects', [{
      name: 'Agent Software Studio',
      initial: 'A',
      color: 'currentColor',
      totalJobs: 3,
      laneCounts: { ready: 1, progress: 1, humanReview: 1 },
      isActive: false,
    }]);
    const chatOpened = vi.fn();
    fixture.componentInstance.chatOpened.subscribe(chatOpened);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('No tabs open.');
    expect(fixture.nativeElement.textContent).toContain('Project boards');
    expect(fixture.nativeElement.querySelector('[data-testid="studio-welcome-chat-hint"]')?.textContent)
      .toContain('Open project chat');
    expect(fixture.nativeElement.textContent).not.toContain('New task');
    fixture.nativeElement.querySelector('[data-testid="studio-welcome-open-chat"]')?.click();
    expect(chatOpened).toHaveBeenCalledOnce();
  });

  it('says why the list is empty when the shell names the reason (AGT-3668)', async () => {
    await TestBed.configureTestingModule({
      imports: [StudioWelcomeComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();

    const fixture = TestBed.createComponent(StudioWelcomeComponent);
    fixture.componentRef.setInput('projects', []);
    fixture.detectChanges();
    const empty = () => fixture.nativeElement.querySelector('[data-testid="studio-welcome-no-projects"]')?.textContent?.trim();
    expect(empty()).toBe('No projects loaded. Check the backend connection.');

    fixture.componentRef.setInput('emptyMessage', 'You have no project yet; ask a workspace owner to add you to one.');
    fixture.detectChanges();
    expect(empty()).toBe('You have no project yet; ask a workspace owner to add you to one.');
  });
});
