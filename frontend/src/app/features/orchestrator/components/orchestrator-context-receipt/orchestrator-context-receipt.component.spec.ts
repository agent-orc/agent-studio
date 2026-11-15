import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { OrchestratorContextReceiptComponent, contextSourceStatusTone } from './orchestrator-context-receipt.component';

describe('OrchestratorContextReceiptComponent', () => {
  it('discloses included, excerpted, and unresolved source truth', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorContextReceiptComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorContextReceiptComponent);
    fixture.componentRef.setInput('receipt', {
      scope: 'project', contextKey: 'project:Demo', taskKey: null,
      includedBlocks: ['file:Demo/README.md'], capturedAt: '2026-08-10T10:00:00Z',
      receiptId: 'rcp_1234567890abcdef', userTurnId: 'user_1',
      budget: { automaticSoftCapTokens: 4000, automaticHardCapTokens: 6000, totalHardCapTokens: 8000, estimatedIncludedTokens: 1720 },
      sources: [
        { sourceId: 'file:Demo/README.md', kind: 'repository-file', revision: '0123456789abcdef', sha256: 'abc', freshness: 'current', includedCharacters: 4000, estimatedTokens: 1000, status: 'included' },
        { sourceId: 'page:Demo/concept.md', kind: 'page', revision: null, sha256: 'def', freshness: 'current', includedCharacters: 2800, estimatedTokens: 700, status: 'excerpted', reason: 'Bounded to the submitted budget.' },
        { sourceId: 'task:Demo/DEMO-9/bundle', kind: 'task-bundle', revision: null, sha256: null, freshness: 'unknown', includedCharacters: 0, estimatedTokens: 0, status: 'unresolved', reason: 'Task not found.' },
      ],
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="orch-context-inspector"]')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-testid="orch-context-inspect-toggle"]')!.click();
    fixture.detectChanges();

    const inspector = root.querySelector('[data-testid="orch-context-inspector"]')!;
    expect(inspector.textContent).toContain('Included');
    expect(inspector.textContent).toContain('Excerpted');
    expect(inspector.textContent).toContain('Unresolved');
    expect(inspector.textContent).toContain('Task not found.');
    const tone = (sourceId: string) =>
      (inspector.querySelector(`[data-testid="orch-context-receipt-status-${sourceId}"]`) as HTMLElement).dataset['tone'];
    expect(tone('file:Demo/README.md')).toBe('success');
    expect(tone('page:Demo/concept.md')).toBe('warning');
    expect(tone('task:Demo/DEMO-9/bundle')).toBe('danger');
  });

  it('maps every context source status onto a tone and falls back to neutral', () => {
    expect(contextSourceStatusTone('included')).toBe('success');
    expect(contextSourceStatusTone('excerpted')).toBe('warning');
    for (const status of ['unresolved', 'unavailable', 'blocked', 'oversize']) {
      expect(contextSourceStatusTone(status)).toBe('danger');
    }
    expect(contextSourceStatusTone('omitted-budget')).toBe('neutral');
    expect(contextSourceStatusTone('something-new')).toBe('neutral');
  });
});
