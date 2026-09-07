import { describe, expect, it } from 'vitest';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CliModelsPanelComponent } from './cli-models-panel';
import { CLI_TYPES, type CliType } from '../../../../models/task.model';
import type { CliModelRoutesResponse } from '../../../quota';

const catalogueProfile = {
  cliType: 'codex',
  primaryModel: 'gpt-5.6-sol',
  primaryThinkingLevel: 'high',
  fallbackCliType: 'claude',
  fallbackModel: 'claude-opus-5',
  fallbackThinkingLevel: 'high',
  fallbackDisabled: false,
  fallbackSource: 'catalogue' as const,
};

const baseRoutes: CliModelRoutesResponse = {
  profiles: { codex: catalogueProfile },
  activeFallbacks: [],
};

async function renderPanel(routes: CliModelRoutesResponse = baseRoutes): Promise<{
  fixture: ComponentFixture<CliModelsPanelComponent>;
  http: HttpTestingController;
}> {
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
  http.expectOne('/api/cli/quota/model-routes').flush(routes);
  http.expectOne('/api/cli/model-routing/policy').flush({
    policyVersion: '2026-07-24',
    wikiPath: 'docs/system/domains/model-routing-policy.md',
    economyMode: false,
    economyModeLabel: 'Economy mode',
    tiers: [],
    taskTypeDefaults: {},
  });
  for (const cliType of CLI_TYPES) {
    http.expectOne(`/api/cli/${cliType}/models`).flush(modelCatalog(cliType));
  }
  fixture.detectChanges();
  return { fixture, http };
}

function modelCatalog(cliType: CliType) {
  const models = cliType === 'codex'
    ? [{ id: 'gpt-5.6-sol', label: 'GPT 5.6 Sol', isDefault: true, thinkingLevels: ['medium', 'high'] }]
    : cliType === 'claude'
      ? [{ id: 'claude-opus-5', label: 'Claude Opus 5', isDefault: true, thinkingLevels: ['medium', 'high'] }]
      : [{ id: 'gemini-pro', label: 'Gemini Pro', isDefault: true, thinkingLevels: ['high'] }];
  return {
    source: 'test',
    models: models.map((model) => ({ ...model, multiplier: 1, vendor: cliType })),
  };
}

describe('CliModelsPanelComponent', () => {
  it('compiles, produces one group per known CLI, and saves economy mode', async () => {
    const { fixture, http } = await renderPanel();

    const groups = fixture.componentInstance.groups();
    expect(groups.length).toBe(CLI_TYPES.length);
    expect(groups.map((group) => group.cliType)).toContain('claude');
    expect(groups.every((group) => group.label.length > 0)).toBe(true);

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
    http.verify();
  });

  it('shows the active provider switch with route, reason, and activation time', async () => {
    const { fixture, http } = await renderPanel({
      profiles: { codex: catalogueProfile },
      activeFallbacks: [{
        primaryCliType: 'codex',
        effectiveCliType: 'claude',
        effectiveModel: 'claude-opus-5',
        effectiveThinkingLevel: 'high',
        outcome: 'LaunchFallback',
        reason: 'Codex Weekly is at 98% until the 08:38 reset.',
        activatedAt: '2026-09-07T02:16:00Z',
        resetAt: '2026-09-07T06:38:00Z',
      }],
    });

    const state = fixture.nativeElement.querySelector(
      '[data-testid="cli-models-active-fallback-codex"]',
    ) as HTMLElement;
    expect(state).not.toBeNull();
    expect(state.textContent).toContain('Codex → Claude Code · Claude Opus 5 · high');
    expect(state.textContent).toContain('Codex Weekly is at 98% until the 08:38 reset.');
    expect(state.textContent).toContain('Since ');
    expect(fixture.nativeElement.querySelector('[data-testid="cli-models-active-fallback-claude"]')).toBeNull();
    http.verify();
  });

  it('keeps catalogue, explicit override, and disabled fallback behavior distinct', async () => {
    const { fixture, http } = await renderPanel();
    (fixture.nativeElement.querySelector('[data-testid="cli-models-toggle-codex"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    let mode = fixture.nativeElement.querySelector('[data-testid="cli-fallback-mode-codex"]') as HTMLSelectElement;
    expect(mode.value).toBe('catalogue');
    expect(Array.from(mode.options, (option) => option.text)).toEqual([
      'Catalogue equivalent', 'Explicit override', 'Disabled',
    ]);
    expect(fixture.nativeElement.querySelector('[data-testid="cli-fallback-catalogue-codex"]')?.textContent)
      .toContain('→ Claude Code · Claude Opus 5 · catalogue');

    mode.value = 'override';
    mode.dispatchEvent(new Event('change'));
    const overrideSave = http.expectOne('/api/cli/quota/model-routes');
    expect(overrideSave.request.method).toBe('PUT');
    expect(overrideSave.request.body).toMatchObject({
      cliType: 'codex',
      fallbackCliType: 'claude',
      fallbackModel: 'claude-opus-5',
      fallbackDisabled: false,
      fallbackSource: 'override',
    });
    overrideSave.flush({ ...catalogueProfile, fallbackSource: 'override' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="cli-fallback-cli-codex"]')).not.toBeNull();

    mode = fixture.nativeElement.querySelector('[data-testid="cli-fallback-mode-codex"]') as HTMLSelectElement;
    mode.value = 'disabled';
    mode.dispatchEvent(new Event('change'));
    const disabledSave = http.expectOne('/api/cli/quota/model-routes');
    expect(disabledSave.request.body).toMatchObject({
      fallbackCliType: null,
      fallbackModel: null,
      fallbackThinkingLevel: null,
      fallbackDisabled: true,
      fallbackSource: 'disabled',
    });
    disabledSave.flush({
      ...catalogueProfile,
      fallbackCliType: null,
      fallbackModel: null,
      fallbackThinkingLevel: null,
      fallbackDisabled: true,
      fallbackSource: 'disabled',
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="cli-fallback-disabled-codex"]')?.textContent)
      .toContain('No automatic fallback');

    mode = fixture.nativeElement.querySelector('[data-testid="cli-fallback-mode-codex"]') as HTMLSelectElement;
    mode.value = 'catalogue';
    mode.dispatchEvent(new Event('change'));
    const catalogueSave = http.expectOne('/api/cli/quota/model-routes');
    expect(catalogueSave.request.body).toMatchObject({
      fallbackCliType: null,
      fallbackModel: null,
      fallbackThinkingLevel: null,
      fallbackDisabled: false,
      fallbackSource: 'catalogue',
    });
    catalogueSave.flush(catalogueProfile);
    fixture.detectChanges();
    expect((fixture.nativeElement.querySelector('[data-testid="cli-fallback-mode-codex"]') as HTMLSelectElement).value)
      .toBe('catalogue');
    http.verify();
  });
});
