import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import type { ModelMigrationProposal } from '../../models/model-migration.model';
import { ModelMigrationOfferComponent } from './model-migration-offer.component';

const proposal: ModelMigrationProposal = {
  from: 'claude-sonnet-4-6',
  to: 'claude-sonnet-5',
  family: 'claude-sonnet',
  rule: 'latestInFamily:claude-sonnet',
  catalogVersion: '2026-09-06',
  safeAuto: true,
  targetAvailable: true,
  safeAutoCandidate: true,
  costClassFrom: 'standard',
  costClassTo: 'standard',
  fromReasoningLevels: ['low', 'medium'],
  toReasoningLevels: ['low', 'medium', 'high'],
  ladderCompatible: true,
  note: 'Latest Sonnet generation with a compatible reasoning ladder.',
};

describe('ModelMigrationOfferComponent', () => {
  it('shows the backend impact diff and emits the exact proposal', async () => {
    await TestBed.configureTestingModule({
      imports: [ModelMigrationOfferComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ModelMigrationOfferComponent);
    fixture.componentRef.setInput('proposal', proposal);
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.textContent).toContain('Update available:');
    expect(host.textContent).toContain('claude-sonnet-4-6');
    expect(host.textContent).toContain('claude-sonnet-5');
    expect(host.querySelector('[data-testid="model-migration-impact"]')?.textContent)
      .toContain('standard to standard');
    expect(host.querySelector('[data-testid="model-migration-impact"]')?.textContent)
      .toContain('low, medium to low, medium, high');
    expect(host.querySelector('[data-testid="model-migration-offer"]')?.getAttribute('aria-label')).toBeNull();

    let emitted: ModelMigrationProposal | null = null;
    fixture.componentInstance.applyRequested.subscribe((value) => emitted = value);
    host.querySelector<HTMLButtonElement>('[data-testid="model-migration-offer-apply"]')?.click();
    expect(emitted).toBe(proposal);
  });

  it('does not allow an unavailable target to be applied', async () => {
    await TestBed.configureTestingModule({
      imports: [ModelMigrationOfferComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ModelMigrationOfferComponent);
    fixture.componentRef.setInput('proposal', { ...proposal, targetAvailable: false });
    fixture.detectChanges();

    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    expect(button.textContent).toContain('Unavailable');
  });
});
