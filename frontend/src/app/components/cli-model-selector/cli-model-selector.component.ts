import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import type { CliType } from '../../models/task.model';
import { CLI_TYPES } from '../../models/task.model';
import { CliCatalogStore, orderModelCatalog, type CliModelInfo } from '../../features/cli';
import {
  cliTypeIcon as fmtCliTypeIcon,
  cliTypeLabel as fmtCliTypeLabel,
} from '../../services/format.util';
import { shortModelLabel } from 'coding-agent-chat/core';
import { ModalStackService } from '../../services/modal-stack.service';
import { ConnectedOverlayDirective } from '../../directives/connected-overlay.directive';
import { OverlayPortalDirective } from '../../directives/overlay-portal.directive';
import { AppTooltipDirective } from '../tooltip/app-tooltip.directive';
import { modelAriaLabel, modelAvailabilityNote, moveRadioSelection, normalizeThinkingLevel,
  olderModelAriaLabel, olderModelNote } from './cli-model-selector.util';

interface CliOption {
  id: CliType;
  label: string;
  icon: string;
}

/**
 * Unified Studio CLI + model selector shared by board, task, pipeline, and
 * settings surfaces. The catalog and its lifecycle signals are app-owned:
 *
 * - the CLI vocabulary (`CLI_TYPES` + label/icon formatting),
 * - the catalog data source (`CliCatalogStore`, hydrated per ADR-0046):
 *   the picker refreshes on open and keeps cached data visible,
 * - generation projection: current models lead while deprecated and
 *   convention-derived older generations stay selectable in a quieter group,
 * - the modal stack (Escape/close coordination with app dialogs).
 *
 * Selecting a model or level auto-commits while the CLI is unchanged. A CLI
 * change keeps the picker open until Done so the selection commits atomically.
 */
@Component({
  selector: 'app-cli-model-selector',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AppTooltipDirective, ConnectedOverlayDirective, OverlayPortalDirective],
  templateUrl: './cli-model-selector.component.html',
  styleUrl: './cli-model-selector.component.scss',
})
export class CliModelSelectorComponent {
  readonly cliType = input<CliType | null>(null);
  readonly model = input<string | null>(null);
  readonly thinkingLevel = input<string | null>(null);
  /** Optional snapshot fallback used only before the catalog store has hydrated. */
  readonly availableModels = input<readonly CliModelInfo[]>([]);
  /** True while the surface should suppress the click (e.g. a run is in flight). */
  readonly disabled = input<boolean>(false);
  /** Optional explicit reason shown in the tooltip when `disabled` is true. */
  readonly disabledReason = input<string | null>(null);
  /** Optional eyebrow shown above the current value in the popover header. */
  readonly eyebrow = input<string>('Configure agent');
  /** Override the default chip aria-label ("Model: cli · model"). */
  readonly ariaLabelOverride = input<string | null>(null);
  /** Override the default chip tooltip. Falls back to the canonical "cli · model" text. */
  readonly tooltipOverride = input<string | null>(null);
  /** Testid for the trigger chip; call-sites pass their legacy value (e.g. "chat-compose-model"). */
  readonly triggerTestid = input<string>('cli-model-selector-trigger');
  /** Prefix for every testid inside the popover; legacy call-sites pass e.g. "chat-model-picker". */
  readonly pickerTestidPrefix = input<string>('cli-model-selector-picker');

  /** Atomic commit: emitted from Done or from an auto-commit on a model click. */
  readonly commit = output<{ cliType: CliType; model: string; thinkingLevel: string | null }>();
  readonly cliTypeChange = output<CliType>();
  readonly modelChange = output<string>();
  readonly thinkingLevelChange = output<string | null>();

  private readonly modalStack = inject(ModalStackService);
  private readonly catalogStore = inject(CliCatalogStore);
  private readonly destroyRef = inject(DestroyRef);
  private modalStackDispose: (() => void) | null = null;

  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');
  readonly triggerEl = computed(() => this.trigger()?.nativeElement ?? null);
  readonly pickerOpen = signal(false);
  readonly draftCliType = signal<CliType | null>(null);
  readonly draftModel = signal('');
  readonly draftThinkingLevel = signal<string | null>(null);
  readonly draftModels = signal<readonly CliModelInfo[]>([]);
  private readonly draftModelPinned = signal(true);

  readonly cliOptions = computed<readonly CliOption[]>(() =>
    CLI_TYPES.map((t) => ({ id: t, label: fmtCliTypeLabel(t), icon: fmtCliTypeIcon(t) })),
  );

  /** Catalog answer for the library's latest `catalogRequested` CLI. */
  private readonly catalogModels = signal<readonly CliModelInfo[] | null>(null);
  readonly catalogLoading = signal<boolean>(false);
  readonly catalogError = signal<string | null>(null);
  /** Which CLI the in-flight/last catalog answer belongs to — drops stale responses. */
  private lastRequestedCli: CliType | null = null;

  readonly effectiveModels = computed<readonly CliModelInfo[]>(
    () => orderModelCatalog(this.catalogModels() ?? this.availableModels()),
  );
  readonly draftAvailableModels = computed(() => this.draftModels());
  readonly currentModels = computed(() =>
    this.draftModels().filter((model) => !model.deprecated && !model.olderGeneration),
  );
  readonly olderModels = computed(() =>
    this.draftModels().filter((model) => Boolean(model.deprecated || model.olderGeneration)),
  );
  readonly draftSelectedModel = computed(() => {
    const id = this.draftModel();
    return id ? this.draftModels().find((model) => model.id === id) ?? null : null;
  });
  readonly draftThinkingLevels = computed(
    () => this.draftSelectedModel()?.thinkingLevels ?? [],
  );
  readonly effectiveDisabledReason = computed(() => {
    if (!this.disabled()) return null;
    return this.disabledReason() ?? 'Stop the run first to change the model.';
  });
  readonly displayName = computed(() => shortModelLabel(this.model()));
  readonly cliIcon = computed(() => {
    const cli = this.cliType();
    return cli ? this.cliOptions().find((option) => option.id === cli)?.icon ?? '·' : '·';
  });
  readonly currentBadgeText = computed(() =>
    this.badgeText(this.cliType(), this.model(), this.thinkingLevel()),
  );
  readonly draftHeaderText = computed(() =>
    this.badgeText(
      this.draftCliType(),
      this.draftModel().length > 0 ? this.draftModel() : null,
      this.draftThinkingLevel(),
    ),
  );
  readonly tooltip = computed(() => {
    const override = this.tooltipOverride();
    if (override !== null) return override;
    const reason = this.effectiveDisabledReason();
    return reason
      ? `${this.currentBadgeText()}\n${reason}`
      : `${this.currentBadgeText()} - click to change`;
  });
  readonly ariaLabel = computed(
    () => this.ariaLabelOverride() ?? `Model: ${this.currentBadgeText()}`,
  );
  readonly hasChanges = computed(() => {
    if (!this.pickerOpen()) return false;
    const cliChanged = this.draftCliType() !== this.cliType();
    const modelChanged = this.draftModel() !== (this.model() ?? '').trim();
    const currentLevel = normalizeThinkingLevel(
      this.draftModels(),
      this.draftModel(),
      this.thinkingLevel(),
    );
    return cliChanged || modelChanged || this.draftThinkingLevel() !== currentLevel;
  });

  constructor() {
    effect(() => {
      const open = this.pickerOpen();
      if (open) {
        this.acquireModalStack();
      } else {
        this.releaseModalStack();
      }
    });

    effect(() => {
      if (!this.disabled() || !this.pickerOpen()) return;
      untracked(() => this.closePicker());
    });

    effect(() => {
      const models = this.effectiveModels();
      if (!this.pickerOpen()) return;
      untracked(() => this.applyCatalog(models));
    });
  }

  onCatalogRequested(cli: string): void {
    const t = cli as CliType;
    this.lastRequestedCli = t;
    this.catalogError.set(null);
    if (this.catalogStore.hasFresh(t)) {
      this.catalogLoading.set(false);
      this.catalogModels.set(this.catalogStore.modelsFor(t));
      const refresh = this.catalogStore.refreshForPickerOpen(t);
      refresh?.subscribe({
        next: (models) => {
          if (this.lastRequestedCli !== t) return;
          this.catalogModels.set(models);
        },
        // Keep the synchronous cached catalog visible; explicit Refresh
        // still surfaces errors when the operator asks for it.
        error: () => void 0,
      });
      return;
    }
    this.catalogLoading.set(true);
    this.catalogModels.set([]);
    this.catalogStore.ensure(t).subscribe({
      next: (models) => {
        if (this.lastRequestedCli !== t) return;
        this.catalogLoading.set(false);
        this.catalogModels.set(models);
      },
      error: () => {
        if (this.lastRequestedCli !== t) return;
        this.catalogLoading.set(false);
        this.catalogModels.set([]);
        this.catalogError.set(
          'Could not load the model catalog for this CLI. Pick another CLI or try again.',
        );
      },
    });
  }

  onRefreshRequested(cli: string): void {
    const t = cli as CliType;
    this.lastRequestedCli = t;
    this.catalogError.set(null);
    this.catalogLoading.set(true);
    this.catalogStore.refresh(t).subscribe({
      next: (models) => {
        if (this.lastRequestedCli !== t) return;
        this.catalogLoading.set(false);
        this.catalogModels.set(models);
      },
      error: () => {
        if (this.lastRequestedCli !== t) return;
        this.catalogLoading.set(false);
        this.catalogError.set('Could not refresh the model catalog. Try again in a moment.');
      },
    });
  }

  openPicker(event: Event): void {
    if (this.effectiveDisabledReason() !== null) return;
    event.preventDefault();
    event.stopPropagation();
    const currentModel = (this.model() ?? '').trim();
    this.draftCliType.set(this.cliType());
    this.draftModel.set(currentModel);
    this.draftModelPinned.set(true);
    this.draftModels.set(this.effectiveModels());
    this.draftThinkingLevel.set(
      normalizeThinkingLevel(this.draftModels(), currentModel, this.thinkingLevel()),
    );
    this.pickerOpen.set(true);
    const cli = this.cliType();
    if (cli) this.onCatalogRequested(cli);
  }

  closePicker(): void {
    this.pickerOpen.set(false);
    queueMicrotask(() => this.trigger()?.nativeElement.focus());
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.pickerOpen()) this.closePicker();
  }

  onCliPillClick(cli: CliType): void {
    if (cli !== this.draftCliType()) {
      this.draftCliType.set(cli);
      this.draftModels.set([]);
      this.draftModel.set('');
      this.draftThinkingLevel.set(null);
      this.draftModelPinned.set(false);
    }
    this.onCatalogRequested(cli);
  }

  onCliPillKeydown(cli: CliType, event: KeyboardEvent): void {
    moveRadioSelection(
      event,
      this.cliOptions().map((option) => option.id),
      cli,
      (next) => this.onCliPillClick(next),
    );
  }

  onModelPillClick(modelId: string): void {
    if (this.draftModels().find((model) => model.id === modelId)?.available === false) return;
    const previousLevel = this.draftThinkingLevel();
    this.draftModel.set(modelId);
    this.draftModelPinned.set(true);
    this.draftThinkingLevel.set(
      normalizeThinkingLevel(this.draftModels(), modelId, previousLevel),
    );
    if (this.draftCliType() === this.cliType()) this.onDoneClick();
  }

  onDefaultModelClick(): void {
    this.draftModel.set('');
    this.draftModelPinned.set(true);
    this.draftThinkingLevel.set(null);
    if (this.draftCliType() === this.cliType()) this.onDoneClick();
  }

  onModelPillKeydown(modelId: string, event: KeyboardEvent): void {
    moveRadioSelection(
      event,
      ['', ...this.draftModels().filter((model) => model.available !== false).map((model) => model.id)],
      modelId,
      (next) => next === '' ? this.onDefaultModelClick() : this.onModelPillClick(next),
    );
  }

  onThinkingLevelPillClick(level: string): void {
    this.draftThinkingLevel.set(level);
    if (this.draftCliType() === this.cliType()) this.onDoneClick();
  }

  onThinkingLevelPillKeydown(level: string, event: KeyboardEvent): void {
    moveRadioSelection(
      event,
      [...this.draftThinkingLevels()],
      level,
      (next) => this.onThinkingLevelPillClick(next),
    );
  }

  onDoneClick(): void {
    if (!this.pickerOpen()) return;
    if (this.disabled()) {
      this.closePicker();
      return;
    }
    const cli = this.draftCliType();
    if (!cli) {
      this.closePicker();
      return;
    }
    if (this.hasChanges()) {
      const selection = {
        cliType: cli,
        model: this.draftModel(),
        thinkingLevel: this.draftThinkingLevel(),
      };
      if (cli !== this.cliType()) this.cliTypeChange.emit(cli);
      this.modelChange.emit(selection.model);
      this.thinkingLevelChange.emit(selection.thinkingLevel);
      this.commit.emit(selection);
    }
    this.closePicker();
  }

  onRefreshClick(): void {
    const cli = this.draftCliType();
    if (cli) this.onRefreshRequested(cli);
  }

  readonly modelAvailabilityNote = modelAvailabilityNote;
  readonly modelAriaLabel = modelAriaLabel;
  readonly olderModelNote = olderModelNote;
  readonly olderModelAriaLabel = olderModelAriaLabel;
  private applyCatalog(models: readonly CliModelInfo[]): void {
    const selectable = models;
    this.draftModels.set(selectable);
    const current = this.draftModel();
    const stillValid = current === '' || selectable.some((model) => model.id === current);
    if (this.draftModelPinned() && stillValid) {
      this.draftThinkingLevel.set(
        normalizeThinkingLevel(this.draftModels(), current, this.draftThinkingLevel()),
      );
      return;
    }
    const defaultModel = selectable.find((model) => model.available !== false && model.isDefault)
      ?? selectable.find((model) => model.available !== false);
    this.draftModel.set(defaultModel?.id ?? '');
    this.draftThinkingLevel.set(defaultModel?.defaultThinkingLevel ?? null);
  }

  private badgeText(
    cliType: CliType | null,
    model: string | null,
    thinkingLevel: string | null,
  ): string {
    const cli = cliType
      ? this.cliOptions().find((option) => option.id === cliType)?.label ?? cliType
      : 'no CLI';
    const modelLabel = model?.trim() || 'CLI default';
    return thinkingLevel
      ? `${cli} · ${modelLabel} · ${thinkingLevel}`
      : `${cli} · ${modelLabel}`;
  }

  private acquireModalStack(): void {
    if (this.modalStackDispose !== null) return;
    this.modalStackDispose = this.modalStack.pushUntilDestroyed(
      'cli-model-selector-picker',
      () => {
        this.closePicker();
        return true;
      },
      this.destroyRef,
    );
  }

  private releaseModalStack(): void {
    if (this.modalStackDispose === null) return;
    this.modalStackDispose();
    this.modalStackDispose = null;
  }
}
