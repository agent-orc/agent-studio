import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProtocolVerdictBannerComponent } from './protocol-verdict-banner.component';
import type { ProtocolVerdict } from '../protocol-verdict';

function verdict(overrides: Partial<ProtocolVerdict> = {}): ProtocolVerdict {
  return {
    kind: 'ok',
    status: 'succeeded',
    signals: [],
    emoji: '🟢',
    label: 'Success',
    detail: 'Last run completed successfully.',
    lane: null,
    duration: null,
    ...overrides,
  };
}

async function build(v: ProtocolVerdict) {
  await TestBed.configureTestingModule({
    imports: [ProtocolVerdictBannerComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ProtocolVerdictBannerComponent);
  fixture.componentRef.setInput('verdict', v);
  fixture.detectChanges();
  return fixture;
}

const LONG_REASON =
  'Rewrote protocol-verdict.ts rendering all five canonical states, but could not verify the banner end to end.';

describe('ProtocolVerdictBannerComponent', () => {
  it('renders one primary banner and keeps conflicting raw signals in Why this status', async () => {
    const fixture = await build(verdict({
      kind: 'problem',
      status: 'failed',
      label: 'Watchdog timeout',
      detail: 'The run will finalize as failed.',
      signals: [
        { source: 'runner', status: 'failed', label: 'Watchdog timeout', detail: 'The run will finalize as failed.' },
        { source: 'review', status: 'succeeded', label: 'Review accepted', detail: 'Accepted.' },
        { source: 'status', status: 'needs-decision', label: 'Partial', detail: 'Result: Partial.' },
      ],
    }));
    const html = fixture.nativeElement as HTMLElement;

    expect(html.querySelectorAll('.protocol-verdict')).toHaveLength(1);
    expect(html.querySelector('[data-testid="protocol-verdict-signals-list"]')).toBeNull();
    html.querySelector<HTMLButtonElement>('[data-testid="protocol-verdict-signals-toggle"]')!.click();
    fixture.detectChanges();
    const disclosure = html.querySelector('[data-testid="protocol-verdict-signals-list"]');
    expect(disclosure?.textContent).toContain('Watchdog timeout');
    expect(disclosure?.textContent).toContain('Review accepted');
    expect(disclosure?.textContent).toContain('Partial');
  });

  it('renders a short reason as a plain, non-expandable line', async () => {
    const fixture = await build(verdict());
    const html = fixture.nativeElement as HTMLElement;
    expect(fixture.componentInstance.expandable()).toBe(false);
    const detail = html.querySelector('[data-testid="protocol-verdict-detail"]');
    expect(detail).not.toBeNull();
    expect(detail!.tagName).toBe('SPAN');
    expect(detail!.textContent).toContain('completed successfully');
  });

  it('keeps interim status in the running banner and allows dismissing it', async () => {
    const fixture = await build(verdict({ kind: 'unclear', label: 'Running' }));
    fixture.componentRef.setInput('running', true);
    fixture.componentRef.setInput('canRequestInterim', true);
    fixture.detectChanges();
    const html = fixture.nativeElement as HTMLElement;
    let requested = false;
    fixture.componentInstance.requestInterim.subscribe(() => (requested = true));

    html.querySelector<HTMLButtonElement>('[data-testid="protocol-interim-summary"]')!.click();
    expect(requested).toBe(true);
    html.querySelector<HTMLButtonElement>('[data-testid="protocol-running-dismiss"]')!.click();
    fixture.detectChanges();
    expect(html.querySelector('[data-testid="protocol-verdict-unclear"]')).toBeNull();
  });

  it('offers an expand toggle for a long reason and unclamps on click (BEFUND 1)', async () => {
    const fixture = await build(verdict({ kind: 'problem', emoji: '🔴', label: 'Blocked', detail: LONG_REASON }));
    const c = fixture.componentInstance;
    const html = fixture.nativeElement as HTMLElement;
    expect(c.expandable()).toBe(true);
    const btn = html.querySelector<HTMLButtonElement>('[data-testid="protocol-verdict-detail"]');
    expect(btn?.tagName).toBe('BUTTON');
    expect(html.querySelector('.protocol-verdict--expanded')).toBeNull();

    btn!.click();
    fixture.detectChanges();
    expect(c.expanded()).toBe(true);
    expect(html.querySelector('.protocol-verdict--expanded')).not.toBeNull();
    // The whole reason is present in the DOM (clamped only visually).
    expect(html.querySelector('[data-testid="protocol-verdict-detail"]')!.textContent)
      .toContain('could not verify the banner end to end');
  });

  it('collapses again when the verdict content changes', async () => {
    const fixture = await build(verdict({ label: 'Blocked', kind: 'problem', detail: LONG_REASON }));
    const c = fixture.componentInstance;
    c.toggle();
    fixture.detectChanges();
    expect(c.expanded()).toBe(true);

    fixture.componentRef.setInput('verdict', verdict({ label: 'Done', kind: 'ok', detail: LONG_REASON + ' extra' }));
    fixture.detectChanges();
    expect(c.expanded()).toBe(false);
  });
});
