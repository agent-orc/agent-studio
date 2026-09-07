import { describe, expect, it, vi } from 'vitest';
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

  it('shows the active provider switch, reason, route source, and activation time', async () => {
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

    fixture.componentInstance.routes.set({
      codex: {
        cliType: 'codex',
        primaryModel: 'gpt-5.6-sol',
        primaryThinkingLevel: 'high',
        fallbackCliType: 'claude',
        fallbackModel: 'claude-opus-5',
        fallbackThinkingLevel: 'high',
        routeSource: 'catalogue-derived',
        activeFallback: {
          requestedCliType: 'codex',
          requestedModel: 'gpt-5.6-sol',
          requestedThinkingLevel: 'high',
          effectiveCliType: 'claude',
          effectiveModel: 'claude-opus-5',
          effectiveThinkingLevel: 'high',
          reason: 'Codex is at 98% until its weekly reset.',
          activatedAt: '2026-09-07T02:37:00Z',
          resetAt: '2026-09-07T06:38:00Z',
        },
      },
    });
    fixture.detectChanges();

    const active = fixture.nativeElement.querySelector(
      '[data-testid="cli-models-active-fallback-codex"]',
    ) as HTMLElement | null;
    expect(active?.textContent).toContain('Codex · gpt-5.6-sol · high');
    expect(active?.textContent).toContain('Claude Code · claude-opus-5 · high');
    expect(active?.textContent).toContain('Codex is at 98%');
    expect(active?.textContent).toContain('catalogue-derived');
    expect(active?.textContent).toContain('since');
    expect(active?.querySelector('time')?.getAttribute('datetime')).toBe('2026-09-07T02:37:00Z');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="cli-models-active-fallback-claude"]',
    )).toBeNull();
  });

  it('shows catalogue equivalence routes without synthesizing an explicit profile', async () => {
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

    TestBed.inject(HttpTestingController).expectOne('/api/cli/quota/model-routes').flush({
      profiles: {},
      catalogueRoutes: [{
        tierId: 'balanced',
        primaryCliType: 'codex',
        primaryModel: 'gpt-5.4-mini',
        primaryThinkingLevel: 'high',
        fallbackCliType: 'claude',
        fallbackModel: 'claude-sonnet-5',
        fallbackThinkingLevel: 'medium',
        evidenceStatus: 'verified',
        provisional: false,
        policyVersion: '2026-09-07',
        reason: 'Equivalent correctness tier.',
      }],
    });
    fixture.componentInstance.toggle('codex');
    fixture.detectChanges();

    const codex = fixture.nativeElement.querySelector(
      '[data-testid="cli-models-card-codex"]',
    ) as HTMLElement;
    expect(codex.textContent).toContain('catalogue-derived · 1 equivalence tier');
    expect(codex.querySelector(
      '[data-testid="cli-models-catalogue-route-source-codex"]',
    )?.textContent).toContain('Catalogue-derived by equivalence tier · 1 eligible route');
    expect(fixture.componentInstance.routes()['codex']).toBeUndefined();
    expect(fixture.componentInstance.fallbackCli('codex')).toBe('codex');
  });

  it('preserves catalogue routing for primary edits and makes fallback edits explicit', async () => {
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
      catalogueRoutes: [{
        tierId: 'strong', primaryCliType: 'codex', primaryModel: 'gpt-5.6-sol',
        primaryThinkingLevel: 'high', fallbackCliType: 'claude', fallbackModel: 'claude-opus-5',
        fallbackThinkingLevel: 'high', evidenceStatus: 'verified', provisional: false,
        policyVersion: '2026-09-07', reason: 'Equivalent correctness tier.',
      }],
    });

    fixture.componentInstance.setPrimary('codex', 'gpt-5.6-sol');
    const primarySave = http.expectOne('/api/cli/quota/model-routes');
    expect(primarySave.request.method).toBe('PUT');
    expect(primarySave.request.body.routeSource).toBe('catalogue');
    primarySave.flush({
      ...primarySave.request.body,
      fallbackCliType: null,
      fallbackModel: null,
      fallbackThinkingLevel: null,
      routeSource: 'catalogue',
    });

    fixture.componentInstance.setFallbackCli('codex', 'claude');
    const overrideSave = http.expectOne('/api/cli/quota/model-routes');
    expect(overrideSave.request.body).toMatchObject({
      fallbackCliType: 'claude',
      routeSource: 'operator-override',
    });
    overrideSave.flush({ ...overrideSave.request.body, routeSource: 'operator-override' });
    expect(fixture.componentInstance.canUseCatalogueFallback('codex')).toBe(true);

    fixture.componentInstance.useCatalogueFallback('codex');
    const resetSave = http.expectOne('/api/cli/quota/model-routes');
    expect(resetSave.request.body).toMatchObject({
      fallbackCliType: null,
      fallbackModel: null,
      fallbackThinkingLevel: null,
      routeSource: 'catalogue',
    });
    resetSave.flush({ ...resetSave.request.body, routeSource: 'catalogue' });
  });

  it('refreshes route admission state on the quota polling cadence', async () => {
    vi.useFakeTimers();
    try {
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
      http.expectOne('/api/cli/quota/model-routes').flush({ profiles: {} });

      vi.advanceTimersByTime(60_000);

      http.expectOne('/api/cli/quota/model-routes').flush({ profiles: {} });
      fixture.destroy();
    } finally {
      vi.useRealTimers();
    }
  });
});
