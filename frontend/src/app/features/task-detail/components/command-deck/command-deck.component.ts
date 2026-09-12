import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { BetterModelCandidate, CliType, WatchPathEntry } from '../../../../models/task.model';
import type { CliModelInfo } from '../../../../features/cli';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';

import { TooltipDirective } from 'coding-agent-chat/shared';
/**
 * Top toolbar of the job-detail view: project picker, unified CLI+model
 * selector chip, and the live elapsed-time / start / stop controls.
 *
 * Stateless / presentational: the parent owns the model + CLI selection
 * (via two-way `model` bindings), watchPaths (list), and the run-state
 * signals; this component only emits intent (start/stop, change project).
 *
 * Migrated 2026-05-29 to use the shared `<app-cli-model-selector>` chip
 * (see `docs/quality/frontend/audits/cli-model-selector-audit.md`) so the toolbar matches the
 * chat composer, overview Agent row, create-task dialog, status-bar
 * default pickers, and code-review panel.
 */
@Component({
  selector: 'app-command-deck',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, TooltipDirective, CliModelSelectorComponent],
  templateUrl: './command-deck.component.html',
  styleUrls: ['./command-deck.component.scss']
})
export class CommandDeckComponent {
  readonly currentWatchPath = input.required<string>();
  readonly watchPaths = input<WatchPathEntry[]>([]);
  readonly cliTypeDraft = input.required<CliType>();
  readonly modelDraft = input.required<string>();
  readonly thinkingLevelDraft = input<string | null>(null);
  readonly availableModels = input<CliModelInfo[]>([]);
  readonly betterCandidates = input<readonly BetterModelCandidate[]>([]);

  readonly isRunning = input(false);
  readonly canStart = input(false);
  readonly startDisabledReason = input<string | null>(null);
  readonly starting = input(false);
  readonly elapsedTime = input('');
  /** Compact mode: hides selectors and shows a "Show setup" toggle.
   *  Owned by the parent (auto-collapsed while a run is active, manually
   *  re-expandable). The component just renders what it's told. */
  readonly collapsed = input(false);

  readonly projectChange = output<string>();
  readonly cliTypeChange = output<CliType>();
  readonly modelChange = output<string>();
  readonly thinkingLevelChange = output<string | null>();
  readonly startRequest = output<void>();
  readonly stopRequest = output<void>();
  readonly toggleCollapsed = output<void>();
}
