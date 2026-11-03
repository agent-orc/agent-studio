import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { STATUS_TONE_ICONS, StatusIndicatorComponent, type StatusTone } from './status-indicator.component';

describe('StatusIndicatorComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [StatusIndicatorComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  function render(inputs: { tone: StatusTone; label: string; detail?: string | null; compact?: boolean }) {
    const fixture = TestBed.createComponent(StatusIndicatorComponent);
    fixture.componentRef.setInput('tone', inputs.tone);
    fixture.componentRef.setInput('label', inputs.label);
    if (inputs.detail !== undefined) fixture.componentRef.setInput('detail', inputs.detail);
    if (inputs.compact !== undefined) fixture.componentRef.setInput('compact', inputs.compact);
    fixture.detectChanges();
    const root = fixture.nativeElement.querySelector('[data-testid="status-indicator"]') as HTMLElement;
    return { fixture, root };
  }

  const expectedIcons: Record<StatusTone, string> = {
    success: 'check',
    warning: 'warn',
    danger: 'close',
    info: 'info',
    neutral: 'circle',
    running: 'spinner',
  };

  for (const tone of Object.keys(expectedIcons) as StatusTone[]) {
    it(`renders the ${tone} tone with its own icon in front of the label`, () => {
      const { root } = render({ tone, label: `State ${tone}` });

      expect(root.dataset['tone']).toBe(tone);
      expect(root.dataset['icon']).toBe(expectedIcons[tone]);
      const [icon, label] = Array.from(root.children) as HTMLElement[];
      expect(icon.classList.contains('status-indicator__icon')).toBe(true);
      expect(icon.querySelector('svg')).not.toBeNull();
      expect(label.textContent?.trim()).toBe(`State ${tone}`);
    });
  }

  it('gives every tone a distinct icon so colour is never the only carrier', () => {
    const icons = Object.values(STATUS_TONE_ICONS);
    expect(new Set(icons).size).toBe(icons.length);
  });

  it('hides the icon from assistive technology and keeps the text readable', () => {
    const { root } = render({ tone: 'success', label: 'Connected' });

    const icon = root.querySelector('.status-indicator__icon') as HTMLElement;
    expect(icon.getAttribute('aria-hidden')).toBe('true');
    expect(icon.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
    const label = root.querySelector('.status-indicator__label') as HTMLElement;
    expect(label.getAttribute('aria-hidden')).toBeNull();
    expect(label.closest('[aria-hidden="true"]')).toBeNull();
    expect(root.textContent?.trim()).toBe('Connected');
  });

  it('shows the detail as a focusable tooltip only when there is one', () => {
    const plain = render({ tone: 'warning', label: 'Needs confirmation' }).root;
    expect(plain.getAttribute('tabindex')).toBeNull();

    vi.useFakeTimers();
    try {
      const { root } = render({ tone: 'warning', label: 'Needs confirmation', detail: 'Confirm the installation on GitHub.' });
      expect(root.getAttribute('tabindex')).toBe('0');
      root.dispatchEvent(new FocusEvent('focusin'));
      vi.advanceTimersByTime(400);
      const tooltip = document.querySelector('.app-tooltip-overlay') as HTMLElement;
      expect(tooltip?.textContent).toContain('Confirm the installation on GitHub.');
      expect(root.getAttribute('aria-describedby')).toBe(tooltip.id);
      root.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    } finally {
      vi.useRealTimers();
    }
  });

  it('treats a blank detail as no detail', () => {
    const { root } = render({ tone: 'info', label: 'Optional', detail: '   ' });
    expect(root.getAttribute('tabindex')).toBeNull();
  });

  it('renders the compact form with the same icon and text', () => {
    const { root } = render({ tone: 'danger', label: 'Failed', compact: true });

    expect(root.classList.contains('status-indicator--compact')).toBe(true);
    expect(root.dataset['icon']).toBe('close');
    expect(root.textContent?.trim()).toBe('Failed');
  });

  it('uses the default chip form unless compact is requested', () => {
    const { root } = render({ tone: 'neutral', label: 'Not set' });
    expect(root.classList.contains('status-indicator--compact')).toBe(false);
  });
});
