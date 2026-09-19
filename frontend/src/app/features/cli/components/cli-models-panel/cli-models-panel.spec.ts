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
    http.expectOne('/api/cli/quota/model-routes').flush({
      profiles: {},
      catalogueVersion: 'TokenEconomy 0.3.4; routing 2026-07-24',
      states: {
        claude: { cliType: 'claude', state: 'normal', activeSince: null, preferenceExpiresAt: null, windows: [] },
      },
      callersCannotReroute: [
        { caller: 'quota-probe', cliType: 'provider-native', reason: 'Provider-native introspection.' },
      ],
      routes: [{
        fromCliType: 'claude', fromModel: 'claude-opus-5', fromThinkingLevel: 'high',
        toCliType: 'codex', toModel: 'gpt-5.6-sol', toThinkingLevel: 'high',
        source: 'catalogue', catalogueVersion: 'TokenEconomy 0.3.4; routing 2026-07-24',
        fromInputPerMTok: 15, fromOutputPerMTok: 75,
        toInputPerMTok: 2, toOutputPerMTok: 8,
        capabilityClass: 'High',
        reroutable: true,
      }],
    });
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
    const fallbackTable = fixture.nativeElement.querySelector('[data-testid="quota-fallback-routes"]');
    expect(fallbackTable?.textContent).toContain('claude-opus-5 high');
    expect(fallbackTable?.textContent).toContain('gpt-5.6-sol high');
    expect(fallbackTable?.textContent).toContain('TokenEconomy 0.3.4');
    expect(fixture.nativeElement.querySelector('[data-testid="quota-non-reroutable-callers"]')?.textContent)
      .toContain('quota-probe');
    economy.click();
    const save = http.expectOne('/api/cli/model-routing/economy-mode');
    expect(save.request.method).toBe('PUT');
    expect(save.request.body).toEqual({ enabled: true });
    save.flush({ economyMode: true });
    fixture.detectChanges();
    expect(fixture.componentInstance.policy()?.economyMode).toBe(true);
  });
});
