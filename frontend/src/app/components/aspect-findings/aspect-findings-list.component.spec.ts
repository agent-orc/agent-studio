import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { AspectFindingsListComponent } from './aspect-findings-list.component';
import type { AspectFinding } from './aspect-findings.model';

describe('AspectFindingsListComponent', () => {
  async function mount(findings: AspectFinding[], leadLabel?: string) {
    await TestBed.configureTestingModule({
      imports: [AspectFindingsListComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(AspectFindingsListComponent);
    fixture.componentRef.setInput('findings', findings);
    if (leadLabel !== undefined) fixture.componentRef.setInput('leadLabel', leadLabel);
    fixture.detectChanges();
    return fixture;
  }

  it('renders one row per finding with aspect, chip token, and reason', async () => {
    const fixture = await mount([
      { aspect: 'requirement-fit', verdict: 'concerns', reason: 'missing edge-case test' },
      { aspect: 'code-quality', verdict: 'block', reason: 'helper duplicated' },
    ]);
    const rows = fixture.nativeElement.querySelectorAll('[data-testid="aspect-finding"]');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('requirement-fit');
    expect(rows[0].textContent).toContain('concerns');
    expect(rows[0].textContent).toContain('missing edge-case test');
  });

  it('tones the chip by verdict through the shared status indicator', async () => {
    const fixture = await mount([
      { aspect: 'a', verdict: 'pass', reason: 'ok' },
      { aspect: 'b', verdict: 'concerns', reason: 'meh' },
      { aspect: 'c', verdict: 'block', reason: 'no' },
    ]);
    const chips = fixture.nativeElement.querySelectorAll('[data-testid="aspect-finding-chip"]');
    expect(chips[0].getAttribute('data-tone')).toBe('success');
    expect(chips[1].getAttribute('data-tone')).toBe('warning');
    expect(chips[2].getAttribute('data-tone')).toBe('danger');
    expect(chips[0].textContent?.trim()).toBe('pass');
    expect(chips[2].classList.contains('status-indicator--compact')).toBe(true);
  });

  it('renders no raw ** or [] markdown — the chip shows the bare token', async () => {
    const fixture = await mount([
      { aspect: 'requirement-fit', verdict: 'block', reason: 'gap' },
    ]);
    const text = fixture.nativeElement.textContent ?? '';
    expect(text).not.toContain('**');
    expect(text).not.toContain('[block]');
  });

  it('shows the lead label when provided', async () => {
    const fixture = await mount(
      [{ aspect: 'a', verdict: 'pass', reason: 'ok' }],
      'Gap',
    );
    const lead = fixture.nativeElement.querySelector('.aspect-findings__lead');
    expect(lead?.textContent?.trim()).toBe('Gap');
  });

  it('renders nothing when the findings array is empty', async () => {
    const fixture = await mount([]);
    expect(fixture.nativeElement.querySelector('[data-testid="aspect-findings"]')).toBeNull();
  });
});
