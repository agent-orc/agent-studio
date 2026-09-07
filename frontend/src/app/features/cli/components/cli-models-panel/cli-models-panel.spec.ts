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
      migrationCatalogVersion: '2026-09-06',
      autoModelMigrationsEnabled: true,
      configurationPins: [{
        id: 'summary', label: 'Summary generation', configKey: 'ClaudeCli:SummaryModel',
        currentModel: 'claude-sonnet-4-6',
        modelMigration: {
          from: 'claude-sonnet-4-6', to: 'claude-sonnet-5', family: 'claude-sonnet',
          rule: 'latestInFamily:claude-sonnet', catalogVersion: '2026-09-06',
          safeAuto: true, targetAvailable: true, safeAutoCandidate: true,
          costClassFrom: 'standard', costClassTo: 'standard',
          fromReasoningLevels: ['medium'], toReasoningLevels: ['medium', 'high'],
          ladderCompatible: true, note: 'Compatible upgrade.',
        },
      }],
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

    const auto = fixture.nativeElement.querySelector(
      '[data-testid="model-routing-auto-migrations"]',
    ) as HTMLInputElement;
    expect(auto.checked).toBe(true);
    auto.click();
    const autoSave = http.expectOne('/api/cli/model-routing/auto-migrations');
    expect(autoSave.request.body).toEqual({ enabled: false });
    autoSave.flush({ economyMode: true, autoModelMigrationsEnabled: false });
    fixture.detectChanges();
    expect(fixture.componentInstance.policy()?.autoModelMigrationsEnabled).toBe(false);

    expect(fixture.nativeElement.querySelector('[data-testid="model-migration-catalog-version"]')?.textContent)
      .toContain('2026-09-06');
    fixture.nativeElement.querySelector('[data-testid="configuration-pin-migration-summary-apply"]')?.click();
    const apply = http.expectOne('/api/cli/model-routing/configuration-pins/summary/apply');
    expect(apply.request.body).toEqual({
      expectedFrom: 'claude-sonnet-4-6',
      toModel: 'claude-sonnet-5',
      catalogVersion: '2026-09-06',
      rule: 'latestInFamily:claude-sonnet',
    });
    apply.flush({ id: 'summary', currentModel: 'claude-sonnet-5', modelMigration: null });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="model-configuration-pin-summary"]')?.textContent)
      .toContain('Current: claude-sonnet-5');
  });
});
