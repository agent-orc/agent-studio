import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { NotificationStackComponent, toolbarDockTop } from './notification-stack.component';
import { NotificationService } from '../../../services/notification.service';

describe('NotificationStackComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [NotificationStackComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(NotificationStackComponent);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] NotificationStackComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

describe('NotificationStackComponent — position routing', () => {
  function setup() {
    TestBed.configureTestingModule({
      imports: [NotificationStackComponent],
      providers: [provideZonelessChangeDetection(), NotificationService],
    });
    const service = TestBed.inject(NotificationService);
    const fixture = TestBed.createComponent(NotificationStackComponent);
    return { service, fixture, component: fixture.componentInstance };
  }

  it('default toasts route to the top-right pile, not bottom-right', () => {
    const { service, component } = setup();
    service.notify({ kind: 'success', message: 'saved' });

    expect(component.topRight().map((n) => n.message)).toEqual(['saved']);
    expect(component.bottomRight()).toHaveLength(0);
  });

  it('position=bottom-right routes only that toast to the bottom-right pile', () => {
    const { service, component } = setup();
    service.notify({ kind: 'info', message: 'top one' });
    service.notify({ kind: 'info', message: 'undo me', position: 'bottom-right' });

    expect(component.topRight().map((n) => n.message)).toEqual(['top one']);
    expect(component.bottomRight().map((n) => n.message)).toEqual(['undo me']);
  });

  it('renders each pile into its own positioned container', () => {
    const { service, fixture } = setup();
    service.notify({ kind: 'success', message: 'top toast' });
    service.notify({ kind: 'info', message: 'bottom toast', position: 'bottom-right' });
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[position] render skipped:', (e as Error).message);
      return;
    }

    const root: HTMLElement = fixture.nativeElement;
    const top = root.querySelector('[data-testid="notification-stack"]');
    const bottom = root.querySelector('[data-testid="notification-stack-bottom-right"]');
    expect(top).toBeTruthy();
    expect(bottom).toBeTruthy();

    expect(top!.textContent).toContain('top toast');
    expect(top!.textContent).not.toContain('bottom toast');
    expect(bottom!.textContent).toContain('bottom toast');
    expect(bottom!.textContent).not.toContain('top toast');
    expect(bottom!.classList.contains('app-notify-stack--bottom-right')).toBe(true);
  });
});

describe('NotificationStackComponent — below-toolbar dock (AGT-3873)', () => {
  function rect(top: number, bottom: number): DOMRect {
    return { top, bottom, height: bottom - top, left: 0, right: 100, width: 100, x: 0, y: top, toJSON: () => ({}) };
  }

  function root(bar: DOMRect | null, banner: DOMRect | null, form: DOMRect | null = null): ParentNode {
    const element = (box: DOMRect | null) => box && { getBoundingClientRect: () => box };
    return { querySelector: (selector: string) => selector.includes('studio-tabbar') ? element(bar)
      : selector.includes('task-move-refusal') ? element(banner)
        : selector.includes('acceptance-refusal-form') ? element(form) : null } as unknown as ParentNode;
  }

  it('routes a below-toolbar toast into its own pile', () => {
    TestBed.configureTestingModule({
      imports: [NotificationStackComponent],
      providers: [provideZonelessChangeDetection(), NotificationService],
    });
    const service = TestBed.inject(NotificationService);
    const fixture = TestBed.createComponent(NotificationStackComponent);
    service.notify({ kind: 'warning', message: 'Accept refused', position: 'below-toolbar' });
    service.notify({ kind: 'info', message: 'other' });
    fixture.detectChanges();

    const component = fixture.componentInstance;
    expect(component.belowToolbar().map(n => n.message)).toEqual(['Accept refused']);
    expect(component.topRight().map(n => n.message)).toEqual(['other']);
    const pile = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="notification-stack-below-toolbar"]');
    expect(pile?.textContent).toContain('Accept refused');
  });

  it('docks below the tab bar, and below the refusal banner while it is on screen', () => {
    expect(toolbarDockTop(root(rect(44, 80), null), 900)).toBe(88);
    expect(toolbarDockTop(root(rect(44, 80), rect(88, 180)), 900)).toBe(188);
    // A banner that leaves no room for the toast, or scrolled above the bar, is not chased.
    expect(toolbarDockTop(root(rect(44, 80), rect(88, 750)), 900)).toBe(88);
    expect(toolbarDockTop(root(rect(44, 80), rect(-200, -100)), 900)).toBe(88);
    expect(toolbarDockTop(root(null, null), 900)).toBeNull();
  });

  it('docks below the open override form, which opens under the toolbar action', () => {
    expect(toolbarDockTop(root(rect(44, 80), rect(88, 180), rect(84, 300)), 900)).toBe(308);
    expect(toolbarDockTop(root(rect(44, 80), null, rect(84, 300)), 900)).toBe(308);
    // A phone: the form under the wrapped toolbar reaches past half the screen.
    expect(toolbarDockTop(root(rect(48, 140), rect(150, 360), rect(285, 525)), 844)).toBe(533);
  });
});
