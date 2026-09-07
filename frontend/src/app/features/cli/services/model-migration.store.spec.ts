import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ModelMigrationStore } from './model-migration.store';

const VIEW = {
  catalogVersion: '2026-09-06',
  catalogSource: 'token-economy',
  wikiPath: 'docs/system/domains/model-routing-policy.md',
  autoApply: true,
  proposals: {
    'claude-opus-4-8': {
      from: { modelId: 'claude-opus-4-8', label: 'Claude Opus 4.8', inputPricePerMillion: null,
              outputPricePerMillion: null, thinkingLevels: [], defaultThinkingLevel: null },
      to: { modelId: 'claude-opus-5', label: 'Claude Opus 5', inputPricePerMillion: null,
            outputPricePerMillion: null, thinkingLevels: [], defaultThinkingLevel: null },
      rule: 'same-family-newer-generation',
      catalogVersion: '2026-09-06',
      safeAuto: true,
      costClass: 'same' as const,
      ladderCompatible: true,
      reason: '',
      safeAutoBlockedBy: null,
    },
  },
};

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  return { store: TestBed.inject(ModelMigrationStore), http: TestBed.inject(HttpTestingController) };
}

describe('ModelMigrationStore', () => {
  it('shares one request across concurrent consumers', () => {
    const { store, http } = setup();

    store.ensure().subscribe();
    store.ensure().subscribe();
    // Three surfaces ask the same question; opening a board must not fan out
    // one request per visible card.
    http.expectOne('/api/cli/model-migrations').flush(VIEW);

    http.verify();
    expect(store.catalogVersion()).toBe('2026-09-06');
    expect(store.catalogSource()).toBe('token-economy');
  });

  it('looks a proposal up by pinned model id and returns null for a current one', () => {
    const { store, http } = setup();
    store.ensure().subscribe();
    http.expectOne('/api/cli/model-migrations').flush(VIEW);

    expect(store.proposalFor('claude-opus-4-8')?.to.modelId).toBe('claude-opus-5');
    expect(store.proposalFor('claude-opus-5')).toBeNull();
    expect(store.proposalFor(null)).toBeNull();
  });

  it('degrades to no proposals when the catalog cannot be read', () => {
    const { store, http } = setup();
    let emitted: unknown = 'not-called';
    store.ensure().subscribe((view) => (emitted = view));
    http.expectOne('/api/cli/model-migrations').error(new ProgressEvent('failed'));

    // A missing catalog must leave every surface exactly as it is today.
    expect(emitted).toBeNull();
    expect(store.proposalFor('claude-opus-4-8')).toBeNull();
    expect(store.autoApply()).toBe(true);
  });

  it('rolls the switch back when persisting it fails', () => {
    const { store, http } = setup();
    store.ensure().subscribe();
    http.expectOne('/api/cli/model-migrations').flush(VIEW);

    store.setAutoApply(false).subscribe({ error: () => void 0 });
    expect(store.autoApply()).toBe(false);
    http.expectOne('/api/cli/model-migrations/auto-apply').error(new ProgressEvent('failed'));

    expect(store.autoApply()).toBe(true);
  });
});
