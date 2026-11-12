import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { LayoutPanesService } from '../../services/layout-panes.service';
import { GitPaneService } from '../../services/git-pane.service';
import { MobilePaneSwitcherComponent } from './mobile-pane-switcher.component';

describe('MobilePaneSwitcherComponent', () => {
  let savedVisible: string | null;
  beforeEach(() => { savedVisible = localStorage.getItem('taskboard.panesVisible'); });
  afterEach(() => {
    if (savedVisible === null) localStorage.removeItem('taskboard.panesVisible');
    else localStorage.setItem('taskboard.panesVisible', savedVisible);
  });

  it('reveals a hidden pane and refreshes Git on first selection', async () => {
    const refresh = vi.fn();
    await TestBed.configureTestingModule({
      imports: [MobilePaneSwitcherComponent],
      providers: [
        provideZonelessChangeDetection(),
        LayoutPanesService,
        { provide: GitPaneService, useValue: { status: signal(null), refresh } },
      ],
    }).compileComponents();
    const layout = TestBed.inject(LayoutPanesService);
    layout.panesVisible.set({ prompt: true, protocol: true, git: false });
    layout.mobilePane.set('prompt');
    const fixture = TestBed.createComponent(MobilePaneSwitcherComponent);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const git = root.querySelector<HTMLButtonElement>('[data-testid="detail-mobile-pane-git"]')!;
    git.click();
    fixture.detectChanges();
    expect(layout.panesVisible().git).toBe(true);
    expect(layout.mobilePane()).toBe('git');
    expect(git.getAttribute('aria-pressed')).toBe('true');
    expect(refresh).toHaveBeenCalledOnce();
  });
});
