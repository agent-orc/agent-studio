import { describe, expect, it, vi } from 'vitest';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { Subject, of, throwError } from 'rxjs';
import { CliModelSelectorComponent } from './cli-model-selector.component';
import type { CliModelInfo } from '../../features/cli';
import { CliCatalogStore } from '../../features/cli';
import { ModalStackService } from '../../services/modal-stack.service';

/**
 * Studio picker specs: the historical inputs/outputs and testids survive, the
 * catalog flows through `CliCatalogStore`, generations render in separate
 * groups, and the popover participates in the app modal stack.
 */
describe('CliModelSelectorComponent', () => {
  const claudeModels: CliModelInfo[] = [
    { id: 'claude-opus-4-7', label: 'Opus 4.7', multiplier: 5, vendor: 'anthropic', isDefault: true, thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max'], defaultThinkingLevel: 'high' },
    { id: 'claude-sonnet-4-6', label: 'Sonnet 4.6', multiplier: 1, vendor: 'anthropic', isDefault: false, thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'high' },
    { id: 'claude-retired', label: 'Retired', multiplier: null, vendor: 'anthropic', isDefault: false, available: false, deprecated: true },
  ];

  function createStoreMock() {
    return {
      hasFresh: vi.fn().mockReturnValue(true),
      modelsFor: vi.fn().mockReturnValue(claudeModels),
      ensure: vi.fn().mockReturnValue(of(claudeModels)),
      refresh: vi.fn().mockReturnValue(of(claudeModels)),
      refreshForPickerOpen: vi.fn().mockReturnValue(null),
    };
  }

  function createModalStackMock() {
    const dispose = vi.fn();
    return {
      dispose,
      service: { pushUntilDestroyed: vi.fn().mockReturnValue(dispose) },
    };
  }

  async function create(
    inputs: Record<string, unknown>,
    store = createStoreMock(),
    modalStack = createModalStackMock(),
  ): Promise<{
    fixture: ComponentFixture<CliModelSelectorComponent>;
    component: CliModelSelectorComponent;
    store: ReturnType<typeof createStoreMock>;
    modalStack: ReturnType<typeof createModalStackMock>;
  }> {
    await TestBed.configureTestingModule({
      imports: [CliModelSelectorComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: CliCatalogStore, useValue: store },
        { provide: ModalStackService, useValue: modalStack.service },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliModelSelectorComponent);
    for (const [key, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(key, value);
    }
    await fixture.whenStable();
    return { fixture, component: fixture.componentInstance, store, modalStack };
  }

  function openPicker(fixture: ComponentFixture<CliModelSelectorComponent>): void {
    const trigger = fixture.nativeElement.querySelector('[data-testid="cli-model-selector-trigger"]') as HTMLButtonElement;
    trigger.click();
    fixture.detectChanges();
  }

  it('renders the legacy trigger testid with the short model label', async () => {
    const { fixture } = await create({ cliType: 'claude', model: 'claude-opus-4-7', availableModels: claudeModels });
    const chip = fixture.nativeElement.querySelector('[data-testid="cli-model-selector-trigger"]');
    expect(chip).toBeTruthy();
    expect(chip.textContent).toContain('opus 4.7');
  });

  it('surfaces a codex gpt-5.6 catalog with its display label and ultra ladder (AGT-2025)', async () => {
    const codexModels: CliModelInfo[] = [
      { id: 'gpt-5.6-sol', label: 'GPT-5.6-Sol', multiplier: null, vendor: 'openai', isDefault: true, thinkingLevels: ['minimal', 'low', 'medium', 'high', 'xhigh', 'ultra'], defaultThinkingLevel: 'ultra' },
      { id: 'gpt-5.5', label: 'GPT-5.5', multiplier: null, vendor: 'openai', isDefault: false, thinkingLevels: ['minimal', 'low', 'medium', 'high', 'xhigh'], defaultThinkingLevel: 'xhigh' },
    ];
    const store = createStoreMock();
    store.modelsFor.mockReturnValue(codexModels);
    store.ensure.mockReturnValue(of(codexModels));
    const { fixture, component } = await create({ cliType: 'codex', model: 'gpt-5.6-sol' }, store);

    openPicker(fixture);
    await fixture.whenStable();
    const sol = component.draftAvailableModels().find((m) => m.id === 'gpt-5.6-sol');
    expect(sol).toBeTruthy();
    expect(sol!.label).toBe('GPT-5.6-Sol');
    expect(sol!.thinkingLevels).toContain('ultra');
  });

  it('passes leading generations first while keeping older models selectable', async () => {
    const unsortedModels: CliModelInfo[] = [
      { ...claudeModels[0], id: 'claude-opus-4-7', label: 'Opus 4.7', isDefault: false },
      { ...claudeModels[0], id: 'claude-opus-5', label: 'Opus 5', isDefault: true },
      { ...claudeModels[0], id: 'claude-opus-4-8', label: 'Opus 4.8', isDefault: false },
    ];
    const store = createStoreMock();
    store.modelsFor.mockReturnValue(unsortedModels);
    store.ensure.mockReturnValue(of(unsortedModels));
    const { fixture, component } = await create(
      { cliType: 'claude', model: 'claude-opus-5' },
      store,
    );

    openPicker(fixture);
    await fixture.whenStable();

    expect(component.draftAvailableModels().map((item) => item.id)).toEqual([
      'claude-opus-5',
      'claude-opus-4-8',
      'claude-opus-4-7',
    ]);
    expect(component.draftAvailableModels().slice(1)).toEqual([
      expect.objectContaining({
        id: 'claude-opus-4-8',
        olderGeneration: true,
        availabilityNote: 'Older generation',
      }),
      expect.objectContaining({
        id: 'claude-opus-4-7',
        olderGeneration: true,
        availabilityNote: 'Older generation',
      }),
    ]);
    expect(component.draftAvailableModels().slice(1).every((item) => !item.deprecated)).toBe(true);
    expect(component.draftAvailableModels().every((item) => item.available !== false)).toBe(true);

    const olderHeading = document.querySelector(
      '[data-testid="cli-model-selector-picker-older-heading"]',
    );
    const olderRows = document.querySelectorAll<HTMLButtonElement>(
      '[data-generation="older"][role="radio"]',
    );
    expect(olderHeading?.textContent).toContain('Older models');
    expect(olderRows).toHaveLength(2);
    expect(olderRows[0].getAttribute('data-deprecated')).toBeNull();
    expect(olderRows[0].textContent).toContain('Older generation');
    expect(olderRows[0].disabled).toBe(false);

    const commits: string[] = [];
    component.modelChange.subscribe((modelId) => commits.push(modelId));
    olderRows[0].click();
    expect(commits).toEqual(['claude-opus-4-8']);
  });

  it('orders the Claude 5 family above 4.x and exposes the Fable ladder', async () => {
    const models: CliModelInfo[] = [
      { ...claudeModels[0], id: 'claude-haiku-4-5', label: 'Claude Haiku 4.5', isDefault: false },
      { ...claudeModels[0], id: 'claude-sonnet-5', label: 'Claude Sonnet 5', isDefault: false },
      { ...claudeModels[0], id: 'claude-fable-5-1', label: 'Claude Fable 5.1', isDefault: true,
        thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max'], defaultThinkingLevel: 'high' },
      { ...claudeModels[0], id: 'claude-opus-5', label: 'Claude Opus 5', isDefault: false },
      { ...claudeModels[0], id: 'claude-opus-4-8', label: 'Claude Opus 4.8', isDefault: false },
    ];
    const store = createStoreMock();
    store.modelsFor.mockReturnValue(models);
    store.ensure.mockReturnValue(of(models));
    const { fixture, component } = await create(
      { cliType: 'claude', model: 'claude-fable-5-1', thinkingLevel: 'high' },
      store,
    );

    openPicker(fixture);
    await fixture.whenStable();

    expect(component.currentModels().map((model) => model.id)).toEqual([
      'claude-fable-5-1',
      'claude-sonnet-5',
      'claude-opus-5',
    ]);
    expect(component.olderModels().map((model) => model.id)).toEqual([
      'claude-opus-4-8',
      'claude-haiku-4-5',
    ]);
    expect(component.draftThinkingLevels()).toEqual(['low', 'medium', 'high', 'xhigh', 'max']);
  });

  it('keeps a pinned unavailable model visible but disabled with its catalog note', async () => {
    const note = 'Known in registry but not reported by the installed Claude CLI.';
    const models: CliModelInfo[] = [
      { ...claudeModels[0], id: 'claude-opus-5', label: 'Claude Opus 5', isDefault: true },
      { ...claudeModels[0], id: 'claude-fable-5-1', label: 'Claude Fable 5.1',
        isDefault: false, available: false, availabilityNote: note },
    ];
    const store = createStoreMock();
    store.modelsFor.mockReturnValue(models);
    store.ensure.mockReturnValue(of(models));
    const { fixture, component } = await create(
      { cliType: 'claude', model: 'claude-fable-5-1' },
      store,
    );

    openPicker(fixture);
    await fixture.whenStable();

    const unavailable = document.querySelector<HTMLButtonElement>(
      '[data-testid="cli-model-selector-picker-model-claude-fable-5-1"]',
    );
    expect(component.draftModel()).toBe('claude-fable-5-1');
    expect(unavailable?.disabled).toBe(true);
    expect(unavailable?.getAttribute('aria-disabled')).toBe('true');
    expect(unavailable?.textContent).toContain(note);
    unavailable?.click();
    expect(component.pickerOpen()).toBe(true);
  });

  it('serves a fresh catalog from the store and schedules the silent picker-open refresh', async () => {
    const { fixture, component, store } = await create({ cliType: 'claude', model: 'claude-opus-4-7' });
    openPicker(fixture);
    expect(store.modelsFor).toHaveBeenCalledWith('claude');
    expect(store.refreshForPickerOpen).toHaveBeenCalledWith('claude');
    await fixture.whenStable();
    expect(component.draftAvailableModels().map((m) => m.id)).toEqual([
      'claude-opus-4-7',
      'claude-sonnet-4-6',
      'claude-retired',
    ]);
  });

  it('loads via ensure() when the store has no fresh catalog', async () => {
    const store = createStoreMock();
    const pendingCatalog = new Subject<readonly CliModelInfo[]>();
    store.hasFresh.mockReturnValue(false);
    store.ensure.mockReturnValue(pendingCatalog);
    const { fixture, component } = await create({ cliType: 'claude', model: 'claude-opus-4-7' }, store);

    openPicker(fixture);
    expect(store.ensure).toHaveBeenCalledWith('claude');
    expect(fixture.componentInstance.catalogLoading()).toBe(true);

    pendingCatalog.next(claudeModels);
    pendingCatalog.complete();
    await fixture.whenStable();
    expect(fixture.componentInstance.catalogLoading()).toBe(false);
    expect(component.draftAvailableModels().length).toBe(3);
  });

  it('surfaces a catalog error when ensure() fails', async () => {
    const store = createStoreMock();
    store.hasFresh.mockReturnValue(false);
    store.ensure.mockReturnValue(throwError(() => new Error('boom')));
    const { fixture } = await create({ cliType: 'claude', model: 'claude-opus-4-7' }, store);

    openPicker(fixture);
    await fixture.whenStable();
    expect(fixture.componentInstance.catalogError()).toMatch(/could not load/i);
  });

  it('emits an atomic commit with the app CliType payload', async () => {
    const { fixture, component } = await create({ cliType: 'claude', model: 'claude-opus-4-7', thinkingLevel: 'high' });
    const commits: { cliType: string; model: string; thinkingLevel: string | null }[] = [];
    fixture.componentInstance.commit.subscribe((c) => commits.push(c));
    const modelChanges: string[] = [];
    fixture.componentInstance.modelChange.subscribe((m) => modelChanges.push(m));

    openPicker(fixture);
    await fixture.whenStable();
    component.onModelPillClick('claude-sonnet-4-6');

    expect(commits).toEqual([
      { cliType: 'claude', model: 'claude-sonnet-4-6', thinkingLevel: 'high' },
    ]);
    expect(modelChanges).toEqual(['claude-sonnet-4-6']);
  });

  it('pushes onto the modal stack while open and disposes on close', async () => {
    const { fixture, component, modalStack } = await create({ cliType: 'claude', model: 'claude-opus-4-7' });
    openPicker(fixture);
    await fixture.whenStable();
    expect(modalStack.service.pushUntilDestroyed).toHaveBeenCalledTimes(1);

    component.closePicker();
    await fixture.whenStable();
    expect(modalStack.dispose).toHaveBeenCalledTimes(1);
  });

  it('surfaces a refresh error from the explicit Refresh affordance', async () => {
    const store = createStoreMock();
    store.refresh.mockReturnValue(throwError(() => new Error('boom')));
    const { fixture } = await create({ cliType: 'claude', model: 'claude-opus-4-7' }, store);

    openPicker(fixture);
    fixture.componentInstance.onRefreshRequested('claude');
    await fixture.whenStable();
    expect(fixture.componentInstance.catalogError()).toMatch(/could not refresh/i);
  });
});
