import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { ModelMigrationOfferComponent } from './model-migration-offer';
import type { ModelMigrationProposal } from '../../models/model-migration.model';

const proposal: ModelMigrationProposal = {
  scope: 'configuration',
  configKey: 'ClaudeCli:SummaryModel',
  fromModel: 'claude-sonnet-4-6',
  toModel: 'claude-sonnet-5',
  rule: 'same-family-current',
  catalogVersion: '2026-09-06',
  explicit: true,
  costClassFrom: 'standard',
  costClassTo: 'standard',
  reasoningLadderFrom: ['low', 'medium'],
  reasoningLadderTo: ['low', 'medium', 'high'],
};

describe('ModelMigrationOfferComponent', () => {
  it('shows the model, cost, and reasoning diff and emits one-click apply', async () => {
    await TestBed.configureTestingModule({
      imports: [ModelMigrationOfferComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();

    const fixture = TestBed.createComponent(ModelMigrationOfferComponent);
    fixture.componentRef.setInput('proposal', proposal);
    const applied = vi.fn();
    fixture.componentInstance.apply.subscribe(applied);
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.textContent).toContain(
      'Update available: claude-sonnet-4-6 to claude-sonnet-5',
    );
    expect(host.querySelector('[data-testid="model-migration-diff"]')?.textContent)
      .toContain('Cost standard → standard');
    expect(host.querySelector('[data-testid="model-migration-diff"]')?.textContent)
      .toContain('Reasoning low / medium → low / medium / high');

    host.querySelector<HTMLButtonElement>('[data-testid="model-migration-apply"]')?.click();
    expect(applied).toHaveBeenCalledWith(proposal);
  });
});
