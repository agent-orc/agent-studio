import { beforeEach, describe, expect, it, vi } from 'vitest';
import { of, Subject } from 'rxjs';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CliModelsPanelComponent, fallbackStateTone } from './cli-models-panel';
import { CLI_TYPES } from '../../../../models/task.model';
import { QuotaApiService, type CliModelFallbackRoute, type CliModelRouteProfile } from '../../../quota';
import { CliCatalogStore } from '../../services/cli-catalog.store';
import { RoutingCostService } from '../../services/routing-cost.service';
import { TaskService } from '../../../../services/task.service';

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
    const highRoute: CliModelFallbackRoute = {
      fromCliType: 'claude', fromModel: 'claude-opus-5', fromThinkingLevel: 'high',
      toCliType: 'codex', toModel: 'gpt-5.6-sol', toThinkingLevel: 'high',
      source: 'catalogue', catalogueVersion: 'TokenEconomy 0.3.4; routing 2026-07-24',
      fromInputPerMTok: 15, fromOutputPerMTok: 75,
      toInputPerMTok: 2, toOutputPerMTok: 8,
      capabilityClass: 'High', reroutable: true,
    };
    const mediumRoute: CliModelFallbackRoute = {
      ...highRoute,
      fromThinkingLevel: 'medium',
      toThinkingLevel: 'medium',
    };
    http.expectOne('/api/cli/quota/model-routes').flush({
      profiles: {},
      catalogueVersion: 'TokenEconomy 0.3.4; routing 2026-07-24',
      states: {
        claude: { cliType: 'claude', state: 'normal', activeSince: null, preferenceExpiresAt: null, windows: [] },
      },
      callersCannotReroute: [
        { caller: 'quota-probe', cliType: 'provider-native', reason: 'Provider-native introspection.' },
      ],
      routes: [highRoute, mediumRoute],
    });
    http.expectOne('/api/cli/model-routing/policy').flush({
      version: '2026-07-24',
      wikiPath: 'docs/system/domains/model-routing-policy.md',
      economyMode: false,
      economyModeLabel: 'Economy mode',
      tiers: [],
      taskTypeDefaults: {},
    });
    http.expectOne('/api/model-selection/active').flush([{
      id: 'switch-1', kind: 'switched', at: '2026-11-04T12:00:00Z', consumer: 'coding',
      project: 'alpha', taskKey: 'AGT-1',
      from: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' },
      to: { cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high' },
      rule: 'quota-cap', returnToPrimaryAt: '2026-11-07T12:00:00Z',
    }]);
    http.expectOne('/api/model-selection/policy').flush({
      version: 1,
      consumers: {
        'review-grade': {
          primary: { cli: 'claude', model: 'claude-opus-5', thinking: 'high' },
          route: null, fallbacks: [{ cli: 'codex', model: 'gpt-6-sol', thinking: 'high' }],
          sameClassOnly: true, stickyWithinCard: true, costGuardFactor: null,
        },
      },
      caps: {}, costGuardFactor: 2,
      hysteresis: { returnBelowCapPoints: 10, minDwellMinutes: 30 },
      migration: { inheritedSteps: 36, explicitSteps: 36, projects: 12, applied: true },
    });
    http.expectOne('/api/token-pricing/route-models').flush({ models: [] });
    const initialEffective = http.match('/api/model-selection/effective');
    expect(initialEffective).toHaveLength(2);
    for (const request of initialEffective) request.flush({
      routes: [{
        consumer: 'review-grade', stepId: 'post-code-review-grade',
        configured: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' },
        effective: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' },
        rule: 'pin', reason: null,
      }],
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="consumer-policy-review-grade"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="active-model-switches"]')?.textContent)
      .toContain('Active fallbacks (1)');
    expect(fixture.nativeElement.textContent).toContain('36 inherited');

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
    expect(fixture.componentInstance.fallbackRouteKey(highRoute))
      .not.toBe(fixture.componentInstance.fallbackRouteKey(mediumRoute));
    expect(fixture.componentInstance.fallbackRouteKey(highRoute))
      .not.toBe(fixture.componentInstance.fallbackRouteKey({ ...highRoute, toThinkingLevel: 'medium' }));
    expect(fallbackTable?.querySelectorAll('tbody tr')).toHaveLength(2);
    expect(fallbackTable?.textContent).toContain('claude-opus-5 high');
    expect(fallbackTable?.textContent).toContain('claude-opus-5 medium');
    expect(fallbackTable?.textContent).toContain('gpt-5.6-sol high');
    expect(fallbackTable?.textContent).toContain('TokenEconomy 0.3.4');
    const tableScroll = fixture.nativeElement.querySelector('[data-testid="cli-models-table-scroll"]') as HTMLElement;
    expect(tableScroll.tabIndex).toBe(0);
    Object.defineProperties(tableScroll, {
      scrollWidth: { configurable: true, value: 600 },
      clientWidth: { configurable: true, value: 200 },
      scrollHeight: { configurable: true, value: 100 },
      clientHeight: { configurable: true, value: 100 },
    });
    for (const [key, position] of [['End', 400], ['Home', 0], ['PageDown', 200], ['ArrowDown', 248]] as const) {
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
      tableScroll.dispatchEvent(event);
      expect(event.defaultPrevented).toBe(true);
      expect(tableScroll.scrollLeft).toBe(position);
    }
    for (const key of ['Tab', 'Escape']) {
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
      tableScroll.dispatchEvent(event);
      expect(event.defaultPrevented).toBe(false);
    }
    Object.defineProperties(tableScroll, {
      scrollWidth: { configurable: true, value: 200 },
      scrollHeight: { configurable: true, value: 100 },
    });
    for (const key of ['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End']) {
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
      tableScroll.dispatchEvent(event);
      expect(event.defaultPrevented).toBe(false);
    }
    expect(fixture.nativeElement.querySelector('[data-testid="effective-routes-claude"]')?.textContent)
      .toContain('review-grade: claude');
    expect(fixture.nativeElement.querySelector('[data-testid="quota-non-reroutable-callers"]')?.textContent)
      .toContain('quota-probe');
    economy.click();
    fixture.detectChanges();
    http.expectNone('/api/cli/model-routing/economy-mode');
    const saveButton = fixture.nativeElement.querySelector('[data-testid="save-economy-mode"]') as HTMLButtonElement;
    expect(saveButton.disabled).toBe(true);
    const previewButton = [...fixture.nativeElement.querySelectorAll('[data-testid="model-routing-policy"] button')]
      .find((button: unknown) => (button as HTMLButtonElement).textContent?.includes('Preview cost')) as HTMLButtonElement;
    previewButton.click();
    http.expectOne('/api/token-pricing/what-if').flush({ receipts: 0, actualUsd: null, alternativeUsd: null,
      savingUsd: null, savingPercent: null, projectedWeeklySavingUsd: null, unpricedActual: 0, unpricedAlternative: 0,
      coverage: 'complete', qualityCaveat: 'Quality may vary', correctnessFloor: 'Floor applies' });
    fixture.detectChanges();
    saveButton.click();
    const save = http.expectOne('/api/cli/model-routing/economy-mode');
    expect(save.request.method).toBe('PUT');
    expect(save.request.body).toEqual({ enabled: true });
    save.flush({ economyMode: true });
    fixture.detectChanges();
    expect(fixture.componentInstance.policy()?.economyMode).toBe(true);

    fixture.componentInstance.setPreferFallback('claude', true);
    const preference = http.expectOne('/api/cli/quota/fallback-preference');
    expect(preference.request.method).toBe('PUT');
    expect(preference.request.body).toEqual({ cliType: 'claude', preferFallback: true });
    preference.flush({
      cliType: 'claude', active: true,
      enabledAt: '2026-09-25T07:00:00Z', expiresAt: '2026-09-26T07:00:00Z',
    });
    http.expectOne('/api/model-selection/effective').flush({
      routes: [{
        consumer: 'review-grade', stepId: 'post-code-review-grade',
        configured: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' },
        effective: { cliType: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'high' },
        rule: 'operator-switch', reason: 'Operator switch',
      }],
    });
    fixture.detectChanges();
    expect(fixture.componentInstance.fallbackStateLabel('claude'))
      .toContain('fallback preferred by operator until');
    const state = fixture.nativeElement.querySelector('[data-testid="cli-models-fallback-state-claude"]') as HTMLElement;
    expect(state.dataset['tone']).toBe('warning');
    expect(state.textContent).toContain('fallback preferred by operator until');
    expect(fixture.nativeElement.querySelector('[data-testid="effective-routes-claude"]')?.textContent)
      .toContain('review-grade: codex');
  });
});

describe('fallbackStateTone', () => {
  it('maps fallback states to tones and falls back to neutral', () => {
    expect(fallbackStateTone('normal')).toBe('neutral');
    expect(fallbackStateTone('fallback-active')).toBe('warning');
    expect(fallbackStateTone('fallback-preferred')).toBe('warning');
    expect(fallbackStateTone('something-new')).toBe('neutral');
    expect(fallbackStateTone(undefined)).toBe('neutral');
  });
});


describe('CliModelsPanelComponent route saves', () => {
  const profile = (cliType = 'claude'): CliModelRouteProfile => ({
    cliType, primaryModel: 'original', primaryThinkingLevel: 'high',
    fallbackCliType: 'codex', fallbackModel: 'automatic', fallbackThinkingLevel: 'medium',
    isFallbackDerived: true,
  });
  let component: CliModelsPanelComponent;
  let writes: { profile: CliModelRouteProfile; response: Subject<CliModelRouteProfile> }[];

  beforeEach(async () => {
    writes = [];
    await TestBed.configureTestingModule({
      imports: [CliModelsPanelComponent],
      providers: [
        provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
        { provide: CliCatalogStore, useValue: { hydrateAll: vi.fn(), modelsFor: () => [], ensure: () => of([]) } },
        { provide: RoutingCostService, useValue: { models: () => of({ models: [] }) } },
        { provide: TaskService, useValue: { getRegistryWorkspaces: () => of([]), getModelMigrationCatalog: () => of({ migrations: [] }) } },
        { provide: QuotaApiService, useValue: {
          getModelRoutes: () => of({ profiles: { claude: profile(), codex: profile('codex') } }),
          getModelRoutingPolicy: () => of(null),
          getEffectiveModelSelection: () => of({ routes: [] }),
          getActiveModelSwitches: () => of([]),
          setModelRoute: (value: CliModelRouteProfile) => {
            const response = new Subject<CliModelRouteProfile>();
            writes.push({ profile: value, response });
            return response;
          },
        } },
      ],
    }).compileComponents();
    component = TestBed.createComponent(CliModelsPanelComponent).componentInstance;
    component.ngOnInit();
  });

  it.each(['primary', 'fallback'] as const)('rolls back a rejected %s edit and retries it', (field) => {
    if (field === 'primary') component.setPrimary('claude', 'chosen');
    else component.setFallbackModel('claude', 'chosen');
    expect(component.routes()['claude']?.[field === 'primary' ? 'primaryModel' : 'fallbackModel']).toBe('chosen');
    writes[0].response.error(new Error('Rejected'));
    expect(component.routes()['claude']).toEqual(profile());
    expect(component.isSaving('claude')).toBe(false);
    expect(component.routeErrors()['claude']).toBeTruthy();
    component.retryRoute('claude');
    expect(writes).toHaveLength(2);
    expect(writes[1].profile).toEqual(writes[0].profile);
    const returned = { ...writes[1].profile, primaryModel: 'server-value' };
    writes[1].response.next(returned);
    expect(component.routes()['claude']).toEqual(returned);
    expect(component.routeErrors()['claude']).toBeUndefined();
    expect(component.isSaving('claude')).toBe(false);
  });

  it('serializes quick edits against the returned profile while keeping both CLIs independent', () => {
    component.setPrimary('claude', 'first');
    component.setFallbackModel('claude', 'second');
    component.setPrimary('codex', 'other');
    expect(writes).toHaveLength(2);
    expect(component.isSaving('claude')).toBe(true);
    expect(component.isSaving('codex')).toBe(true);
    const returned = { ...profile(), primaryModel: 'canonical-first', primaryThinkingLevel: 'low' };
    writes[0].response.next(returned);
    expect(writes).toHaveLength(3);
    expect(writes[2].profile).toMatchObject({
      primaryModel: 'canonical-first', primaryThinkingLevel: 'low',
      fallbackCliType: 'codex', fallbackModel: 'second',
    });
    expect(component.routes()['claude']?.fallbackModel).toBe('second');
    writes[1].response.error(new Error('Codex rejected'));
    expect(component.isSaving('claude')).toBe(true);
    expect(component.routes()['codex']).toEqual(profile('codex'));
    writes[2].response.error(new Error('Claude rejected'));
    expect(component.routes()['claude']).toEqual(returned);
    component.retryRoute('claude');
    expect(writes[3].profile.primaryModel).toBe('canonical-first');
    expect(writes[3].profile.fallbackModel).toBe('second');
    writes[3].response.next(writes[3].profile);
    expect(component.routeErrors()['codex']).toBeTruthy();
  });

  it('rolls back and retries both quick edits when the first request fails', () => {
    component.setPrimary('claude', 'first');
    component.setFallbackCli('claude', 'gemini');
    component.setFallbackModel('claude', 'second');
    writes[0].response.error(new Error('Rejected'));
    expect(writes).toHaveLength(1);
    expect(component.routes()['claude']).toEqual(profile());
    component.retryRoute('claude');
    expect(writes[1].profile).toMatchObject({ primaryModel: 'first', fallbackCliType: 'gemini', fallbackModel: 'second' });
    writes[1].response.next(writes[1].profile);
    expect(component.isSaving('claude')).toBe(false);
  });

  it('keeps derived fallbacks visible without persisting them on primary edits or retries', () => {
    component.setPrimary('claude', 'first');
    expect(component.routes()['claude']).toMatchObject({ fallbackModel: 'automatic', isFallbackDerived: true });
    expect(writes[0].profile).toMatchObject({ fallbackModel: null, fallbackThinkingLevel: null });
    expect(writes[0].profile.isFallbackDerived).toBeUndefined();
    component.setPrimary('claude', 'second');
    writes[0].response.error(new Error('Rejected'));
    component.retryRoute('claude');
    expect(writes[1].profile).toMatchObject({ primaryModel: 'second', fallbackModel: null, fallbackThinkingLevel: null });
    writes[1].response.next({ ...profile(), primaryModel: 'second' });
    expect(component.fallbackSummary('claude')).toContain('(auto)');
  });
});
