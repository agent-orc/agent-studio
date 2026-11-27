import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AppTooltipDirective } from './app-tooltip.directive';

@Component({
  selector: 'app-tooltip-test-host',
  standalone: true,
  imports: [AppTooltipDirective],
  templateUrl: './app-tooltip.directive.spec.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class TestHostComponent {
  text = '<img src=x onerror=alert(1)> Safe text';
  readonly delay = signal(300);
}

describe('AppTooltipDirective', () => {
  let fixture: ComponentFixture<TestHostComponent>;
  let target: HTMLButtonElement;

  beforeEach(async () => {
    vi.useFakeTimers();
    await TestBed.configureTestingModule({ imports: [TestHostComponent] }).compileComponents();
    fixture = TestBed.createComponent(TestHostComponent);
    fixture.detectChanges();
    target = fixture.nativeElement.querySelector('button');
  });

  afterEach(() => {
    fixture.destroy();
    vi.useRealTimers();
  });

  it('shows escaped text after the hover delay and links it with aria-describedby', () => {
    target.dispatchEvent(new MouseEvent('mouseenter'));
    vi.advanceTimersByTime(299);
    expect(document.querySelector('[role="tooltip"]')).toBeNull();

    vi.advanceTimersByTime(1);
    const overlay = document.querySelector<HTMLElement>('[role="tooltip"]');
    expect(overlay?.textContent).toBe('<img src=x onerror=alert(1)> Safe text');
    expect(overlay?.dataset['testid']).toBe('test-tooltip');
    expect(overlay?.querySelector('img')).toBeNull();
    expect(target.getAttribute('aria-describedby')).toBe(overlay?.id);
  });

  it('closes immediately on Escape and removes its aria description', () => {
    target.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    vi.advanceTimersByTime(300);
    expect(document.querySelector('[role="tooltip"]')).not.toBeNull();

    target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(document.querySelector('[role="tooltip"]')).toBeNull();
    expect(target.hasAttribute('aria-describedby')).toBe(false);
  });

  it('can open immediately for dense status affordances', () => {
    fixture.componentInstance.delay.set(0);
    fixture.detectChanges();

    target.dispatchEvent(new MouseEvent('mouseenter'));

    expect(document.querySelector('[role="tooltip"]')).not.toBeNull();
  });
});
