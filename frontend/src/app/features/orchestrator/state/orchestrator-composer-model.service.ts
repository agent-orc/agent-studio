import { Injectable, computed, inject, signal } from '@angular/core';
import type { ChatModelControl, ChatModelSelection } from 'coding-agent-chat/core';
import { CliCatalogStore } from '../../cli';
import { CLI_TYPES } from '../../../models/task.model';
import { cliTypeIcon, cliTypeLabel } from '../../../services/format.util';
import type { CliModelInfo } from '../../cli';
import { availableCodexModels, resolveComposerSelection } from './orchestrator-composer-model.util';

const GPT_ONLY_REASON = 'Unavailable in this GPT-only chat';
const ORCHESTRATOR_CLI_OPTIONS = CLI_TYPES.map(cliType => ({
  id: cliType,
  label: cliTypeLabel(cliType),
  icon: cliTypeIcon(cliType),
  ...(cliType === 'codex' ? {} : { disabledReason: GPT_ONLY_REASON }),
}));

/**
 * Workspace-persistent model choice for the canonical Orchestrator composer.
 * The operating mode is intentionally Codex-only; model inventory and
 * reasoning ladders remain owned by the live Codex catalogue.
 */
@Injectable({ providedIn: 'root' })
export class OrchestratorComposerModelService {
  private readonly catalogStore = inject(CliCatalogStore);
  private readonly explicitSelection = signal<ChatModelSelection | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly catalog = computed<readonly CliModelInfo[]>(() => {
    return availableCodexModels(this.catalogStore.modelsFor('codex'));
  });

  readonly effectiveSelection = computed<ChatModelSelection>(() => {
    return resolveComposerSelection(
      this.catalog(),
      this.explicitSelection(),
      this.readPreference('defaultModel:codex'),
      this.readPreference('defaultThinkingLevel:codex'),
    );
  });

  readonly selectionSource = computed<'explicit' | 'inherited'>(() => {
    const explicit = this.explicitSelection();
    return explicit && this.catalog().some(model => model.id === explicit.model)
      ? 'explicit'
      : 'inherited';
  });

  readonly control = computed<ChatModelControl>(() => ({
    // Keep the complete task-agent CLI vocabulary visible. This chat route is
    // intentionally GPT-only, so non-Codex choices explain the host policy
    // instead of disappearing as if quota or availability detection failed.
    cliOptions: ORCHESTRATOR_CLI_OPTIONS,
    cliType: 'codex',
    model: this.effectiveSelection().model,
    thinkingLevel: this.effectiveSelection().thinkingLevel,
    catalog: this.catalog(),
    catalogLoading: this.loading(),
    catalogError: this.error(),
  }));

  commit(selection: ChatModelSelection): void {
    if (selection.cliType !== 'codex') return;
    this.explicitSelection.set(resolveComposerSelection(this.catalog(), selection, null, null));
  }

  requestCatalog(refresh = false): void {
    this.loading.set(true);
    this.error.set(null);
    const request = refresh
      ? this.catalogStore.refresh('codex')
      : this.catalogStore.ensure('codex');
    request.subscribe({
      next: () => this.loading.set(false),
      error: () => {
        this.loading.set(false);
        this.error.set('Could not load the Codex model catalogue.');
      },
    });
  }

  private readPreference(key: string): string | null {
    if (typeof window === 'undefined') return null;
    try {
      return window.localStorage?.getItem(key) ?? null;
    } catch {
      return null;
    }
  }
}
