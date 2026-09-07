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
    expect(save.request.body).toEqual({ enabled: true });
    save.flush({ economyMode: true });
    fixture.detectChanges();
    expect(fixture.componentInstance.policy()?.economyMode).toBe(true);
  });

  it('shows the migration catalog, automatic switch, and configuration pin proposal', async () => {
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
    http.expectOne('/api/model-migrations').flush({
      catalogVersion: '2026-09-06',
      catalogSource: 'Token Economy',
      catalogStale: true,
      catalogError: 'fixture path deliberately hidden',
      autoApplySafe: true,
      proposals: [{
        scope: 'configuration', configKey: 'ClaudeCli:SummaryModel',
        fromModel: 'claude-sonnet-4-6', toModel: 'claude-sonnet-5',
        rule: 'same-family-current', catalogVersion: '2026-09-06', explicit: true,
        costClassFrom: 'standard', costClassTo: 'standard',
        reasoningLadderFrom: ['low', 'high'], reasoningLadderTo: ['low', 'high'],
      }],
    });
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-testid="model-migration-catalog-version"]')?.textContent)
      .toContain('2026-09-06');
    expect(host.querySelector('[data-testid="model-migration-catalog-state"]')?.textContent)
      .toContain('Stale');
    expect(host.textContent).not.toContain('fixture path deliberately hidden');
    expect(host.querySelector('[data-testid="configuration-model-migrations"]')?.textContent)
      .toContain('ClaudeCli:SummaryModel');
    expect(host.querySelector('[data-testid="configuration-model-migrations"]')?.textContent)
      .toContain('Update available: claude-sonnet-4-6 to claude-sonnet-5');

    host.querySelector<HTMLButtonElement>('[data-testid="model-migration-apply"]')?.click();
    const apply = http.expectOne('/api/model-migrations/apply');
    expect(apply.request.body).toEqual({
      scope: 'configuration', configKey: 'ClaudeCli:SummaryModel',
      expectedFromModel: 'claude-sonnet-4-6', catalogVersion: '2026-09-06',
    });
    apply.flush({});
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="configuration-model-migrations"]')).toBeNull();

    const automatic = host.querySelector<HTMLInputElement>('[data-testid="model-migration-auto-apply"]');
    expect(automatic?.checked).toBe(true);
    automatic?.click();
    const save = http.expectOne('/api/model-migrations/auto-apply');
    expect(save.request.body).toEqual({ enabled: false });
    save.flush({ autoApplySafe: false });
    fixture.detectChanges();
    expect(fixture.componentInstance.migrationState()?.autoApplySafe).toBe(false);
  });
});
