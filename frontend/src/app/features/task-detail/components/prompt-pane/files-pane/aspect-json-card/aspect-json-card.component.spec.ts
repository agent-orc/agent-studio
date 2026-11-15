import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { AspectJsonCardComponent } from './aspect-json-card.component';
import type { AspectDocument } from '../aspect-document.model';

function doc(overrides: Partial<AspectDocument> = {}): AspectDocument {
  return {
    schemaVersion: 1,
    aspect: 'code-quality',
    status: 'concerns',
    summary: 'Dead helper left behind.',
    details: 'The new file duplicates `foo()`.',
    createdAt: '2026-07-09T19:21:03Z',
    model: 'claude-haiku-4-5',
    tag: 'quality:concerns',
    metrics: null,
    ...overrides,
  };
}

async function render(input: AspectDocument, compact = false) {
  await TestBed.configureTestingModule({
    imports: [AspectJsonCardComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(AspectJsonCardComponent);
  fixture.componentRef.setInput('doc', input);
  fixture.componentRef.setInput('compact', compact);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('AspectJsonCardComponent', () => {
  it('renders a humanised title, tone-mapped status badge, and summary', async () => {
    const root = await render(doc());
    expect(root.querySelector('.aspect-json__title')?.textContent).toContain('Code quality');

    const badge = root.querySelector('[data-testid="aspect-json-status"]');
    expect(badge?.textContent?.trim()).toBe('concerns');
    // concerns → warning tone (shared status indicator mapping).
    expect(badge?.getAttribute('data-tone')).toBe('warning');

    expect(root.querySelector('[data-testid="aspect-json-summary"]')?.textContent)
      .toContain('Dead helper left behind.');
  });

  it('maps pass → success and block → danger tones', async () => {
    const pass = await render(doc({ status: 'pass' }));
    expect(pass.querySelector('[data-testid="aspect-json-status"]')?.getAttribute('data-tone')).toBe('success');

    TestBed.resetTestingModule();
    const block = await render(doc({ status: 'block' }));
    expect(block.querySelector('[data-testid="aspect-json-status"]')?.getAttribute('data-tone')).toBe('danger');
  });

  it('shows the model and a details disclosure in full mode', async () => {
    const root = await render(doc());
    expect(root.querySelector('[data-testid="aspect-json-model"]')?.textContent).toContain('claude-haiku-4-5');
    expect(root.querySelector('[data-testid="aspect-json-details"]')).not.toBeNull();
  });

  it('hides the model, details and metrics in compact (preview) mode', async () => {
    const root = await render(doc({ metrics: { filesChanged: '3' } }), true);
    expect(root.querySelector('[data-testid="aspect-json-model"]')).toBeNull();
    expect(root.querySelector('[data-testid="aspect-json-details"]')).toBeNull();
    expect(root.querySelector('[data-testid="aspect-json-metrics"]')).toBeNull();
    // Summary + badge still visible — that is the shareable at-a-glance line.
    expect(root.querySelector('[data-testid="aspect-json-summary"]')?.textContent).toContain('Dead helper');
  });

  it('renders a metrics strip when metrics are present in full mode', async () => {
    const root = await render(doc({ metrics: { filesChanged: '3', testsPassed: '157' } }));
    const metrics = root.querySelector('[data-testid="aspect-json-metrics"]');
    expect(metrics).not.toBeNull();
    expect(metrics?.textContent).toContain('filesChanged');
    expect(metrics?.textContent).toContain('157');
  });
});
