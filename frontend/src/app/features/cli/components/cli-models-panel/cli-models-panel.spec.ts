import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CliModelsPanelComponent } from './cli-models-panel';
import { CLI_TYPES } from '../../../../models/task.model';

/**
 * Smoke + one behavioural check. Compiles + instantiates the standalone
 * component and asserts it renders one group per known CLI (the groups
 * computed walks CLI_TYPES regardless of whether any catalog is loaded).
 */
describe('CliModelsPanelComponent', () => {
  it('compiles and produces one group per known CLI', async () => {
    await TestBed.configureTestingModule({
      imports: [CliModelsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliModelsPanelComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/cli/model-routing/policy').flush({
      version: '2026-07-24',
      wikiPath: 'docs/system/domains/model-routing-policy.md',
      economyMode: false,
      economyModeLabel: 'Economy mode',
      tiers: [],
      taskTypeDefaults: {},
    });
    fixture.detectChanges();

    // The migration catalog hydrates alongside the routing policy.
    http.expectOne('/api/cli/model-migrations').flush({
      catalogVersion: '2026-09-06',
      catalogSource: 'repository-baseline',
      wikiPath: 'docs/system/domains/model-routing-policy.md',
      autoApply: true,
      proposals: {},
    });
    fixture.detectChanges();

    const groups = fixture.componentInstance.groups();
    expect(groups.length).toBe(CLI_TYPES.length);
    expect(groups.map((g) => g.cliType)).toContain('claude');
    expect(groups.every((g) => typeof g.label === 'string' && g.label.length > 0)).toBe(true);

    const cards = fixture.nativeElement.querySelectorAll('[data-testid^="cli-models-card-"]');
    expect(cards).toHaveLength(CLI_TYPES.length);
    expect(Array.from(cards, (card: Element) => card.getAttribute('data-cli'))).toEqual(CLI_TYPES);

    const economy = fixture.nativeElement.querySelector(
      '[data-testid="model-routing-economy-mode"]',
    ) as HTMLInputElement;
    expect(economy.checked).toBe(false);
    economy.click();
    const save = http.expectOne('/api/cli/model-routing/economy-mode');
    expect(save.request.method).toBe('PUT');
    expect(save.request.body).toEqual({ economyMode: true });
    save.flush({ economyMode: true });
    fixture.detectChanges();
    expect(fixture.componentInstance.policy()?.economyMode).toBe(true);

    // The catalog version has to be visible: it is how an operator tells which
    // rule set produced the proposals they are being offered.
    expect(
      fixture.nativeElement.querySelector('[data-testid="model-migration-catalog-version"]').textContent,
    ).toContain('2026-09-06');

    const autoApply = fixture.nativeElement.querySelector(
      '[data-testid="model-migration-auto-apply"]',
    ) as HTMLInputElement;
    expect(autoApply.checked).toBe(true);
    autoApply.click();
    const switched = http.expectOne('/api/cli/model-migrations/auto-apply');
    expect(switched.request.method).toBe('PUT');
    expect(switched.request.body).toEqual({ autoApply: false });
    switched.flush({ autoApplyModelMigrations: false });
    fixture.detectChanges();
    expect(fixture.componentInstance.migrations.autoApply()).toBe(false);
  });
});
