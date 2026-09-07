import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ModelMigrationHintComponent } from './model-migration-hint.component';
import type { ModelMigrationProposal } from '../../../quota';

function proposal(overrides: Partial<ModelMigrationProposal> = {}): ModelMigrationProposal {
  return {
    from: {
      modelId: 'claude-haiku-4-5',
      label: 'Claude Haiku 4.5',
      inputPricePerMillion: 1,
      outputPricePerMillion: 5,
      thinkingLevels: ['low', 'medium'],
      defaultThinkingLevel: 'medium',
    },
    to: {
      modelId: 'claude-sonnet-5',
      label: 'Claude Sonnet 5',
      inputPricePerMillion: 3,
      outputPricePerMillion: 15,
      thinkingLevels: ['low', 'medium', 'high'],
      defaultThinkingLevel: 'medium',
    },
    rule: 'token-economy-value-tier',
    catalogVersion: '2026-09-06',
    safeAuto: false,
    costClass: 'more-expensive',
    ladderCompatible: true,
    reason: 'Token Economy rates Sonnet 5 as the better value tier.',
    safeAutoBlockedBy: 'catalog',
    ...overrides,
  };
}

/**
 * Renders the hint against a stubbed catalog. The component resolves its own
 * proposal through the shared store, so the fixture seeds the store's one
 * request rather than handing the component a proposal object.
 */
async function render(
  inputs: Record<string, unknown>,
  offered: ModelMigrationProposal | null = proposal(),
) {
  await TestBed.configureTestingModule({
    imports: [ModelMigrationHintComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ModelMigrationHintComponent);
  for (const [key, value] of Object.entries(inputs)) fixture.componentRef.setInput(key, value);
  fixture.detectChanges();
  TestBed.inject(HttpTestingController).expectOne('/api/cli/model-migrations').flush({
    catalogVersion: '2026-09-06',
    catalogSource: 'repository-baseline',
    wikiPath: 'docs/system/domains/model-routing-policy.md',
    autoApply: true,
    proposals: offered === null ? {} : { [offered.from.modelId]: offered },
  });
  fixture.detectChanges();
  return fixture;
}

const PINNED = 'claude-haiku-4-5';

describe('ModelMigrationHintComponent', () => {
  it('renders nothing when the pinned model is current', async () => {
    const fixture = await render({ model: PINNED }, null);

    expect(fixture.nativeElement.querySelector('[data-testid="model-migration-hint"]')).toBeNull();
  });

  it('states the update and whether run admission would apply it', async () => {
    const fixture = await render({ model: PINNED });
    const hint = fixture.nativeElement.querySelector('[data-testid="model-migration-hint"]');

    expect(hint.textContent).toContain('update available: claude-haiku-4-5 to claude-sonnet-5');
    // A cross-family move is offered, never automatic: the operator must see
    // that distinction without opening the tooltip.
    expect(hint.textContent).toContain('needs review');
    expect(hint.getAttribute('data-safe-auto')).toBe('false');
    expect(hint.getAttribute('data-cost-class')).toBe('more-expensive');
  });

  it('labels a safeAuto update as automatic', async () => {
    const fixture = await render(
      { model: PINNED },
      proposal({ safeAuto: true, costClass: 'same', safeAutoBlockedBy: null }),
    );

    expect(
      fixture.nativeElement.querySelector('[data-testid="model-migration-hint"]').textContent,
    ).toContain('automatic');
  });

  it('carries the cost and reasoning-ladder diff so the host renders no policy of its own', async () => {
    const fixture = await render({ model: PINNED });

    const detail = fixture.componentInstance.detail();
    expect(detail).toContain('1/5 per Mtok to 3/15 per Mtok');
    expect(detail).toContain('low · medium to low · medium · high');
    expect(detail).toContain('catalog 2026-09-06');
  });

  it('emits the target model id and leaves the write to the host', async () => {
    const fixture = await render({ model: PINNED });
    const applied: string[] = [];
    fixture.componentInstance.apply.subscribe((model: string) => applied.push(model));

    fixture.nativeElement.querySelector('[data-testid="model-migration-hint-apply"]').click();

    expect(applied).toEqual(['claude-sonnet-5']);
  });

  it('does not emit while the host mutation is in flight', async () => {
    const fixture = await render({ model: PINNED, busy: true });
    const applied: string[] = [];
    fixture.componentInstance.apply.subscribe((model: string) => applied.push(model));

    const button = fixture.nativeElement.querySelector('[data-testid="model-migration-hint-apply"]');
    expect(button.disabled).toBe(true);
    fixture.componentInstance.onApply();

    expect(applied).toEqual([]);
  });

  it('hides the apply control where the host has no mutation to offer', async () => {
    const fixture = await render({ model: PINNED, applyable: false });

    expect(
      fixture.nativeElement.querySelector('[data-testid="model-migration-hint-apply"]'),
    ).toBeNull();
  });
});
