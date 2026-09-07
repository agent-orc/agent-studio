import { signal } from '@angular/core';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { CostBreakdownService } from '../../services/cost-breakdown.service';
import { CostBreakdownDialogComponent } from './cost-breakdown-dialog';

describe('CostBreakdownDialogComponent', () => {
  it('renders rates, counters, formula, source, effective date, and total', async () => {
    const fake = {
      open: signal(true), title: signal('Pipeline cost calculation'), loading: signal(false),
      error: signal<string | null>(null), provider: signal('TokenEconomy'),
      close: () => fake.open.set(false),
      items: signal([{
        model: 'claude-opus-4-7', label: 'Core agent', calculatedAt: '2026-07-11T10:00:00Z',
        inputTokens: 1_000_000, outputTokens: 200_000, cacheReadTokens: 100_000, cacheWriteTokens: 50_000,
        estimate: {
          inputUsd: 5, outputUsd: 5, cacheReadUsd: 0.05, cacheWriteUsd: 0.3125,
          total: 10.3625, modelId: 'claude-opus-4-7', modelKnown: true, status: 'resolved',
          priceBasis: {
            inputPerMillion: 5, outputPerMillion: 25, cacheReadPerMillion: 0.5,
            cacheWritePerMillion: 6.25, currency: 'USD', validFrom: '2026-01-01T00:00:00Z',
            source: 'Anthropic published pricing', note: null, unconfirmed: false,
          },
        },
      }]),
    };
    await TestBed.configureTestingModule({
      imports: [CostBreakdownDialogComponent],
      providers: [provideZonelessChangeDetection(), { provide: CostBreakdownService, useValue: fake }],
    }).compileComponents();
    const fixture = TestBed.createComponent(CostBreakdownDialogComponent);
    fixture.detectChanges();
    const text = document.body.textContent ?? '';
    expect(text).toContain('claude-opus-4-7');
    expect(text).toContain('Input / 1M');
    expect(text).toContain(`${new Intl.NumberFormat().format(1_000_000)} / 1M × $5.00`);
    expect(text).toContain('Anthropic published pricing');
    expect(text).toContain('Price effective date');
    expect(text).toContain('$10.36');
    fixture.destroy();
  });

  it('uses fresh OpenAI input in the formula when recorded input includes cache reads', async () => {
    const fake = {
      open: signal(true), title: signal('GPT-5.5 calculation'), loading: signal(false),
      error: signal<string | null>(null), provider: signal('TokenEconomy'),
      close: () => fake.open.set(false),
      items: signal([{
        model: 'gpt-5.5', label: 'Project runtime', calculatedAt: '2026-09-07T00:00:00Z',
        inputTokens: 10_782_081, outputTokens: 66_760,
        cacheReadTokens: 10_022_528, cacheWriteTokens: 0,
        estimate: {
          inputUsd: 3.797765, outputUsd: 2.0028, cacheReadUsd: 5.011264, cacheWriteUsd: 0,
          total: 10.811829, modelId: 'gpt-5.5', modelKnown: true, status: 'resolved',
          pricedInputTokens: 759_553,
          priceBasis: {
            inputPerMillion: 5, outputPerMillion: 30, cacheReadPerMillion: 0.5,
            cacheWritePerMillion: 5, currency: 'USD', validFrom: '2026-04-24T00:00:00Z',
            source: 'OpenAI published pricing', note: null, unconfirmed: false,
          },
        },
      }]),
    };
    await TestBed.configureTestingModule({
      imports: [CostBreakdownDialogComponent],
      providers: [provideZonelessChangeDetection(), { provide: CostBreakdownService, useValue: fake }],
    }).compileComponents();
    const fixture = TestBed.createComponent(CostBreakdownDialogComponent);
    fixture.detectChanges();
    const text = document.body.textContent ?? '';
    const formattedFreshInput = new Intl.NumberFormat().format(759_553);
    const formattedRecordedInput = new Intl.NumberFormat().format(10_782_081);
    expect(text).toContain(`Pricing uses ${formattedFreshInput} fresh input tokens`);
    expect(text).toContain(`(${formattedFreshInput} / 1M × $5.00)`);
    expect(text).not.toContain(`(${formattedRecordedInput} / 1M × $5.00)`);
    expect(text).toContain('$10.81');
    fixture.destroy();
  });
});
